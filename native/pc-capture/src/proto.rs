pub const PROTO_VERSION: u32 = 12;
pub const SAMPLE_RATE: u32 = 48_000;
pub const CHANNELS: u16 = 1;
pub const FRAME_SAMPLES: usize = 960;
pub const FRAME_BYTES: usize = 8 + FRAME_SAMPLES * 4;

pub const TYPE_CONTROL: u8 = 0x01;
pub const TYPE_AUDIO: u8 = 0x02;
pub const TYPE_AUDIO_OUT: u8 = 0x03;
pub const AUDIO_OUT_FRAMES: usize = 960;
pub const AUDIO_OUT_SAMPLES: usize = AUDIO_OUT_FRAMES * 2;
pub const AUDIO_OUT_BYTES: usize = AUDIO_OUT_SAMPLES * 4;

#[cfg(test)]
use std::io::Read;

#[derive(Debug, Clone)]
pub struct AudioFrame {
    /// Encoder privacy epoch observed when capture of this frame began. Never serialized.
    pub encoder_epoch: u64,
    /// Internal capture lifecycle generation. Not serialized on the legacy PCM wire frame.
    pub capture_generation: u64,
    /// Internal concrete stream-open attempt. Not serialized on the legacy PCM wire frame.
    pub capture_open_attempt: u64,
    /// Process-local monotonic time at which the frame's first sample reached the ADC.
    pub capture_ts_ns: u64,
    /// Process-local monotonic time at which CPAL delivered the buffer containing that sample.
    /// This is internal timing metadata and is intentionally not part of the legacy PCM wire frame.
    pub capture_callback_ts_ns: u64,
    pub capture_timestamp_valid: bool,
    pub samples: Vec<f32>,
}

#[allow(dead_code)]
#[derive(Debug, Clone)]
pub struct AudioOutFrame {
    pub samples: Vec<f32>,
}

#[derive(Debug)]
pub enum Frame {
    Control(String),

    #[allow(dead_code)]
    Audio(AudioFrame),

    #[allow(dead_code)]
    AudioOut(AudioOutFrame),
}

#[derive(Debug)]
pub enum DecodeError {
    Io(std::io::Error),
    BadType(u8),
    BadLen(usize),
    Utf8(std::string::FromUtf8Error),
}

impl std::fmt::Display for DecodeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            DecodeError::Io(e) => write!(f, "io: {e}"),
            DecodeError::BadType(t) => write!(f, "bad frame type: 0x{t:02x}"),
            DecodeError::BadLen(n) => write!(f, "bad frame len: {n}"),
            DecodeError::Utf8(e) => write!(f, "utf8: {e}"),
        }
    }
}

impl std::error::Error for DecodeError {}

impl From<std::io::Error> for DecodeError {
    fn from(e: std::io::Error) -> Self {
        DecodeError::Io(e)
    }
}

pub fn encode_control(json: &str) -> Vec<u8> {
    let body = json.as_bytes();
    let mut out = Vec::with_capacity(5 + body.len());
    out.push(TYPE_CONTROL);
    out.extend_from_slice(&(body.len() as u32).to_le_bytes());
    out.extend_from_slice(body);
    out
}

#[allow(dead_code)]
pub fn encode_audio(frame: &AudioFrame) -> Vec<u8> {
    let mut out = Vec::with_capacity(5 + FRAME_BYTES);
    out.push(TYPE_AUDIO);
    out.extend_from_slice(&(FRAME_BYTES as u32).to_le_bytes());
    out.extend_from_slice(&frame.capture_ts_ns.to_le_bytes());
    for &s in &frame.samples {
        out.extend_from_slice(&s.to_le_bytes());
    }
    out
}

#[cfg(test)]
pub fn read_frame<R: Read>(r: &mut R) -> Result<Frame, DecodeError> {
    let mut header = [0u8; 5];
    r.read_exact(&mut header)?;
    let ftype = header[0];
    let len = u32::from_le_bytes([header[1], header[2], header[3], header[4]]) as usize;
    match ftype {
        TYPE_CONTROL => {
            let mut body = vec![0u8; len];
            r.read_exact(&mut body)?;
            let s = String::from_utf8(body).map_err(DecodeError::Utf8)?;
            Ok(Frame::Control(s))
        }
        TYPE_AUDIO => {
            if len != FRAME_BYTES {
                let mut sink = vec![0u8; len];
                let _ = r.read_exact(&mut sink);
                return Err(DecodeError::BadLen(len));
            }
            let mut body = vec![0u8; FRAME_BYTES];
            r.read_exact(&mut body)?;
            let ts = u64::from_le_bytes(body[0..8].try_into().unwrap());
            let mut samples = Vec::with_capacity(FRAME_SAMPLES);
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
        other => Err(DecodeError::BadType(other)),
    }
}

use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;

#[cfg(not(target_os = "android"))]
use crossbeam_channel::{bounded, Receiver, Sender, TryRecvError, TrySendError};

pub const RING_CAPACITY: usize = 8;

/// Metadata copied into one fixed capture slot. Samples live in the preallocated slot itself so
/// the hardware callback never constructs an `AudioFrame` or allocates its `Vec<f32>`.
#[cfg(not(target_os = "android"))]
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct CaptureFrameMetadata {
    pub encoder_epoch: u64,
    pub capture_generation: u64,
    pub capture_open_attempt: u64,
    pub capture_ts_ns: u64,
    pub capture_callback_ts_ns: u64,
    pub capture_timestamp_valid: bool,
}

#[cfg(not(target_os = "android"))]
struct CaptureSlot {
    sequence: u64,
    metadata: CaptureFrameMetadata,
    samples: [f32; FRAME_SAMPLES],
}

#[cfg(not(target_os = "android"))]
impl CaptureSlot {
    fn new() -> Self {
        Self {
            sequence: 0,
            metadata: CaptureFrameMetadata::default(),
            samples: [0.0; FRAME_SAMPLES],
        }
    }
}

#[cfg(not(target_os = "android"))]
struct CaptureTimestampSlot {
    // Published as logical sequence + 1; zero means this physical slot has never been written.
    sequence_marker: AtomicU64,
    capture_ts_ns: AtomicU64,
}

#[cfg(not(target_os = "android"))]
struct CaptureRingInner {
    capacity: u64,
    ready_tx: Sender<Box<CaptureSlot>>,
    ready_rx: Receiver<Box<CaptureSlot>>,
    free_tx: Sender<Box<CaptureSlot>>,
    free_rx: Receiver<Box<CaptureSlot>>,
    timestamps: Box<[CaptureTimestampSlot]>,
    next_sequence: AtomicU64,
    read_sequence: AtomicU64,
    write_sequence: AtomicU64,
    dropped: AtomicU64,
}

/// Non-blocking producer used only by the active capture callback/thread.
#[cfg(not(target_os = "android"))]
#[derive(Clone)]
pub struct CaptureFrameProducer {
    inner: Arc<CaptureRingInner>,
}

/// Non-blocking consumer/control handle. Clones may safely discard during stop/restart while the
/// encoder owns the normal pop path; the bounded channel arbitrates ownership of each slot.
#[cfg(not(target_os = "android"))]
#[derive(Clone)]
pub struct CaptureFrameConsumer {
    inner: Arc<CaptureRingInner>,
}

#[cfg(not(target_os = "android"))]
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct CaptureRingSnapshot {
    pub len: usize,
    pub capacity: usize,
    pub dropped: u64,
    pub oldest_capture_ts_ns: Option<u64>,
}

/// Creates a bounded latest-audio-wins queue. `capacity + 1` slots are allocated once: the extra
/// slot lets the producer prepare a replacement while the consumer owns one slot, so overflow can
/// always reclaim the oldest queued frame without waiting or allocating.
#[cfg(not(target_os = "android"))]
pub fn capture_frame_ring(capacity: usize) -> (CaptureFrameProducer, CaptureFrameConsumer) {
    let capacity = capacity.max(1);
    let (ready_tx, ready_rx) = bounded(capacity);
    let (free_tx, free_rx) = bounded(capacity + 1);
    for _ in 0..=capacity {
        free_tx
            .try_send(Box::new(CaptureSlot::new()))
            .expect("new capture free-list has exact capacity");
    }
    let inner = Arc::new(CaptureRingInner {
        capacity: capacity as u64,
        ready_tx,
        ready_rx,
        free_tx,
        free_rx,
        timestamps: (0..capacity)
            .map(|_| CaptureTimestampSlot {
                sequence_marker: AtomicU64::new(0),
                capture_ts_ns: AtomicU64::new(0),
            })
            .collect(),
        next_sequence: AtomicU64::new(0),
        read_sequence: AtomicU64::new(0),
        write_sequence: AtomicU64::new(0),
        dropped: AtomicU64::new(0),
    });
    (
        CaptureFrameProducer {
            inner: inner.clone(),
        },
        CaptureFrameConsumer { inner },
    )
}

#[cfg(not(target_os = "android"))]
impl CaptureRingInner {
    fn note_removed(&self, sequence: u64) {
        self.read_sequence
            .fetch_max(sequence.wrapping_add(1), Ordering::AcqRel);
    }

    fn return_free(&self, slot: Box<CaptureSlot>) {
        if let Err(error) = self.free_tx.try_send(slot) {
            // The pool invariant makes this unreachable. Avoid deallocating on a realtime caller
            // even if a future integration bug violates it; leaking one fixed slot is fail-safe.
            std::mem::forget(error.into_inner());
        }
    }

    fn snapshot(&self) -> CaptureRingSnapshot {
        for _ in 0..3 {
            let read = self.read_sequence.load(Ordering::Acquire);
            let write = self.write_sequence.load(Ordering::Acquire);
            let len = write.saturating_sub(read).min(self.capacity) as usize;
            if len == 0 {
                return CaptureRingSnapshot {
                    len: 0,
                    capacity: self.capacity as usize,
                    dropped: self.dropped.load(Ordering::Relaxed),
                    oldest_capture_ts_ns: None,
                };
            }
            let timestamp = &self.timestamps[(read % self.capacity) as usize];
            let marker = timestamp.sequence_marker.load(Ordering::Acquire);
            if marker == read.wrapping_add(1) {
                let capture_ts_ns = timestamp.capture_ts_ns.load(Ordering::Relaxed);
                return CaptureRingSnapshot {
                    len,
                    capacity: self.capacity as usize,
                    dropped: self.dropped.load(Ordering::Relaxed),
                    oldest_capture_ts_ns: (capture_ts_ns != 0).then_some(capture_ts_ns),
                };
            }
        }
        CaptureRingSnapshot {
            len: self
                .write_sequence
                .load(Ordering::Acquire)
                .saturating_sub(self.read_sequence.load(Ordering::Acquire))
                .min(self.capacity) as usize,
            capacity: self.capacity as usize,
            dropped: self.dropped.load(Ordering::Relaxed),
            oldest_capture_ts_ns: None,
        }
    }
}

#[cfg(not(target_os = "android"))]
#[allow(clippy::len_without_is_empty)]
impl CaptureFrameProducer {
    /// Copies one exact 20 ms frame into a preallocated slot. Returns false only for malformed
    /// input or a violated pool invariant; it never blocks, allocates, or logs.
    pub fn push(&self, metadata: CaptureFrameMetadata, samples: &[f32]) -> bool {
        if samples.len() != FRAME_SAMPLES {
            self.inner.dropped.fetch_add(1, Ordering::Relaxed);
            return false;
        }

        let mut slot = match self.inner.free_rx.try_recv() {
            Ok(slot) => slot,
            Err(TryRecvError::Empty) => match self.inner.ready_rx.try_recv() {
                Ok(slot) => {
                    self.inner.note_removed(slot.sequence);
                    self.inner.dropped.fetch_add(1, Ordering::Relaxed);
                    slot
                }
                Err(TryRecvError::Empty | TryRecvError::Disconnected) => {
                    self.inner.dropped.fetch_add(1, Ordering::Relaxed);
                    return false;
                }
            },
            Err(TryRecvError::Disconnected) => return false,
        };

        slot.metadata = metadata;
        slot.samples.copy_from_slice(samples);
        let sequence = self.inner.next_sequence.fetch_add(1, Ordering::Relaxed);
        slot.sequence = sequence;
        let timestamp = &self.inner.timestamps[(sequence % self.inner.capacity) as usize];
        timestamp.capture_ts_ns.store(
            if metadata.capture_timestamp_valid {
                metadata.capture_ts_ns
            } else {
                0
            },
            Ordering::Relaxed,
        );
        timestamp
            .sequence_marker
            .store(sequence.wrapping_add(1), Ordering::Release);

        loop {
            match self.inner.ready_tx.try_send(slot) {
                Ok(()) => {
                    self.inner
                        .write_sequence
                        .store(sequence.wrapping_add(1), Ordering::Release);
                    return true;
                }
                Err(TrySendError::Full(returned)) => {
                    slot = returned;
                    if let Ok(oldest) = self.inner.ready_rx.try_recv() {
                        self.inner.note_removed(oldest.sequence);
                        self.inner.dropped.fetch_add(1, Ordering::Relaxed);
                        self.inner.return_free(oldest);
                    }
                }
                Err(TrySendError::Disconnected(returned)) => {
                    self.inner.return_free(returned);
                    return false;
                }
            }
        }
    }

    pub fn len(&self) -> usize {
        self.inner.snapshot().len
    }
}

#[cfg(not(target_os = "android"))]
#[allow(clippy::len_without_is_empty)]
impl CaptureFrameConsumer {
    /// Copies the next owned slot into a caller-reused frame and immediately recycles the slot.
    pub fn pop_into(&self, frame: &mut AudioFrame) -> bool {
        let Ok(slot) = self.inner.ready_rx.try_recv() else {
            return false;
        };
        self.inner.note_removed(slot.sequence);
        frame.encoder_epoch = slot.metadata.encoder_epoch;
        frame.capture_generation = slot.metadata.capture_generation;
        frame.capture_open_attempt = slot.metadata.capture_open_attempt;
        frame.capture_ts_ns = slot.metadata.capture_ts_ns;
        frame.capture_callback_ts_ns = slot.metadata.capture_callback_ts_ns;
        frame.capture_timestamp_valid = slot.metadata.capture_timestamp_valid;
        if frame.samples.len() != FRAME_SAMPLES {
            frame.samples.resize(FRAME_SAMPLES, 0.0);
        }
        frame.samples.copy_from_slice(&slot.samples);
        self.inner.return_free(slot);
        true
    }

    /// Safe from the control thread during stop/restart. A concurrently checked-out encoder frame
    /// remains protected by its lifecycle generation and cannot be reclaimed out from under it.
    pub fn discard_all(&self) -> usize {
        let mut discarded = 0;
        while let Ok(slot) = self.inner.ready_rx.try_recv() {
            self.inner.note_removed(slot.sequence);
            self.inner.return_free(slot);
            discarded += 1;
        }
        discarded
    }

    /// Privacy-boundary alias: reset queue contents while retaining cumulative drop telemetry.
    pub fn reset(&self) -> usize {
        self.discard_all()
    }

    pub fn snapshot(&self) -> CaptureRingSnapshot {
        self.inner.snapshot()
    }

    pub fn len(&self) -> usize {
        self.snapshot().len
    }

    pub fn capacity(&self) -> usize {
        self.inner.capacity as usize
    }

    pub fn dropped(&self) -> u64 {
        self.inner.dropped.load(Ordering::Relaxed)
    }

    pub fn oldest_capture_ts_ns(&self) -> Option<u64> {
        self.snapshot().oldest_capture_ts_ns
    }
}

/// Preallocated single-producer/single-consumer stereo ring.
///
/// `PlaybackRing` is still commonly wrapped in `Arc<Mutex<_>>` by the control path, but the
/// render callback takes a [`PlaybackConsumer`] once when the stream is opened. From that point
/// on producer and consumer communicate only through atomics; a busy control/statistics lock can
/// no longer turn an entire hardware callback into silence.
pub struct PlaybackRing {
    inner: Arc<PlaybackRingInner>,
}

struct PlaybackRingInner {
    capacity_pairs: u64,
    slots: Box<[AtomicU64]>,
    read_sequence: AtomicU64,
    write_sequence: AtomicU64,
    dropped: AtomicU64,
}

#[derive(Clone)]
pub struct PlaybackConsumer {
    inner: Arc<PlaybackRingInner>,
}

fn pack_stereo(left: f32, right: f32) -> u64 {
    (u64::from(left.to_bits()) << 32) | u64::from(right.to_bits())
}

fn unpack_stereo(pair: u64) -> (f32, f32) {
    (
        f32::from_bits((pair >> 32) as u32),
        f32::from_bits(pair as u32),
    )
}

impl PlaybackRingInner {
    fn len(&self) -> usize {
        let written = self.write_sequence.load(Ordering::Acquire);
        let read = self.read_sequence.load(Ordering::Acquire);
        written.saturating_sub(read).min(self.capacity_pairs) as usize
    }

    fn pop_stereo(&self) -> Option<(f32, f32)> {
        loop {
            let read = self.read_sequence.load(Ordering::Acquire);
            let written = self.write_sequence.load(Ordering::Acquire);
            if read >= written {
                return None;
            }

            // Load before claiming. If the producer had to discard this exact slot in order to
            // make room, its compare-exchange below advances `read_sequence` and this claim
            // fails, so the possibly-overwritten value is never returned.
            let packed = self.slots[(read % self.capacity_pairs) as usize].load(Ordering::Acquire);
            if self
                .read_sequence
                .compare_exchange_weak(read, read + 1, Ordering::AcqRel, Ordering::Acquire)
                .is_ok()
            {
                return Some(unpack_stereo(packed));
            }
        }
    }
}

#[allow(clippy::len_without_is_empty)]
impl PlaybackRing {
    pub fn new(capacity_pairs: usize) -> PlaybackRing {
        let cap = capacity_pairs.max(2);
        PlaybackRing {
            inner: Arc::new(PlaybackRingInner {
                capacity_pairs: cap as u64,
                slots: (0..cap).map(|_| AtomicU64::new(0)).collect(),
                read_sequence: AtomicU64::new(0),
                write_sequence: AtomicU64::new(0),
                dropped: AtomicU64::new(0),
            }),
        }
    }

    pub fn push(&mut self, interleaved: &[f32]) {
        let pairs = interleaved.len() / 2;
        for i in 0..pairs {
            // There is one producer in the IPC/mixer path. The consumer may advance the read
            // sequence concurrently; CAS lets either side win without a lock. On overflow the
            // oldest pair is discarded, preserving the previous latest-audio-wins policy.
            let written = self.inner.write_sequence.load(Ordering::Relaxed);
            loop {
                let read = self.inner.read_sequence.load(Ordering::Acquire);
                if written.saturating_sub(read) < self.inner.capacity_pairs {
                    break;
                }
                if self
                    .inner
                    .read_sequence
                    .compare_exchange_weak(read, read + 1, Ordering::AcqRel, Ordering::Acquire)
                    .is_ok()
                {
                    self.inner.dropped.fetch_add(1, Ordering::Relaxed);
                    break;
                }
            }

            let pair = pack_stereo(interleaved[2 * i], interleaved[2 * i + 1]);
            self.inner.slots[(written % self.inner.capacity_pairs) as usize]
                .store(pair, Ordering::Release);
            self.inner
                .write_sequence
                .store(written + 1, Ordering::Release);
        }
    }

    pub fn pop_stereo(&mut self) -> Option<(f32, f32)> {
        self.inner.pop_stereo()
    }

    pub fn len(&self) -> usize {
        self.inner.len()
    }

    pub fn dropped(&self) -> u64 {
        self.inner.dropped.load(Ordering::Relaxed)
    }

    pub fn consumer(&self) -> PlaybackConsumer {
        PlaybackConsumer {
            inner: self.inner.clone(),
        }
    }
}

#[allow(clippy::len_without_is_empty)]
impl PlaybackConsumer {
    pub fn pop_stereo(&self) -> Option<(f32, f32)> {
        self.inner.pop_stereo()
    }

    pub fn len(&self) -> usize {
        self.inner.len()
    }
}

use serde::{Deserialize, Serialize};

fn default_true() -> bool {
    true
}

#[derive(Debug, Deserialize)]
#[serde(tag = "op")]
pub enum InboundOp {
    #[serde(rename = "hello")]
    Hello { proto: u32, token: String },
    #[serde(rename = "select-device")]
    SelectDevice { id: String },
    #[serde(rename = "start")]
    Start,
    #[serde(rename = "stop")]
    Stop,
    #[serde(rename = "ping")]
    Ping,
    #[serde(rename = "select-output-device")]
    SelectOutputDevice { id: String },
    #[serde(rename = "set-dsp")]
    SetDsp {
        aec: bool,
        agc: bool,
        ns: bool,
        ns_very_high: bool,
        hpf: bool,
    },
    #[serde(rename = "set-diagnostics")]
    SetDiagnostics { enabled: bool },
    #[serde(rename = "set-synthetic")]
    SetSynthetic { enabled: bool },
    #[serde(rename = "set-input")]
    SetInput {
        gain: f32,
        vad_threshold: f32,
        noise_gate_threshold: f32,
    },
    #[serde(rename = "peer-add")]
    PeerAdd {
        peer_id: String,
        #[serde(default = "default_true")]
        offerer: bool,
        #[serde(default)]
        relay_only: bool,
        generation: u32,
    },
    #[serde(rename = "peer-remove")]
    PeerRemove { peer_id: String },
    #[serde(rename = "set-remote-sdp")]
    SetRemoteSdp {
        peer_id: String,
        sdp_type: String,
        sdp: String,
    },
    #[serde(rename = "add-ice-candidate")]
    AddIceCandidate { peer_id: String, candidate: String },
    #[serde(rename = "restart-ice")]
    RestartIce {
        peer_id: String,
        #[serde(default)]
        relay_only: bool,
        #[serde(default = "default_true")]
        create_offer: bool,
    },
    #[serde(rename = "set-ice-servers")]
    SetIceServers { servers: Vec<IceServer> },
    #[serde(rename = "game-state")]
    GameState {
        deaf: bool,
        master: f32,
        peers: Vec<GameStatePeer>,
    },
}

#[derive(Debug, Clone, Deserialize)]
pub struct GameStatePeer {
    pub id: String,
    #[serde(default)]
    pub gain: f32,
    #[serde(default)]
    pub pan: f32,
    #[serde(default)]
    pub mode: i32,
}

#[derive(Debug, Clone, Deserialize)]
pub struct IceServer {
    pub urls: Vec<String>,
    #[serde(default)]
    pub username: Option<String>,
    #[serde(default)]
    pub credential: Option<String>,
}

pub fn parse_inbound(json: &str) -> Result<InboundOp, serde_json::Error> {
    serde_json::from_str(json)
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DeviceInfo {
    pub id: String,
    pub name: String,
    pub default: bool,
}

#[derive(Serialize)]
struct FormatBlock {
    rate: u32,
    channels: u16,
    sample: &'static str,
}

#[derive(Serialize)]
struct ReadyMsg<'a> {
    op: &'static str,
    proto: u32,
    format: FormatBlock,
    devices: &'a [DeviceInfo],
    #[serde(rename = "outputDevices")]
    output_devices: &'a [DeviceInfo],
}

#[derive(Serialize)]
struct DevicesMsg<'a> {
    op: &'static str,
    devices: &'a [DeviceInfo],
    #[serde(rename = "outputDevices")]
    output_devices: &'a [DeviceInfo],
}

#[derive(Serialize)]
struct LevelMsg {
    op: &'static str,
    peak: f32,
    speaking: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct PeerLevel {
    pub peer_id: String,
    pub peak: f32,
}

#[derive(Serialize)]
struct PeerLevelsMsg<'a> {
    op: &'static str,
    levels: &'a [PeerLevel],
}

#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
#[serde(default)]
pub struct NativeStatsSnapshot {
    pub capture_frames: u64,
    pub opus_encoded: u64,
    pub opus_empty: u64,
    pub opus_errors: u64,
    pub rtp_tx_attempts: u64,
    pub rtp_tx_ok: u64,
    pub rtp_tx_errors: u64,
    pub rtp_tx_queue_dropped: u64,
    pub rtp_tx_stale_epoch_dropped: u64,
    pub rtp_tx_write_timeouts: u64,
    pub rtp_tx_queue_depth_max: u64,
    pub rtp_rx_packets: u64,
    pub rtp_rx_bytes: u64,
    pub stale_rtp_rx_dropped: u64,
    pub decode_packets: u64,
    pub decode_frames: u64,
    pub decode_empty: u64,
    pub decode_errors: u64,
    pub peer_level_batches: u64,
    pub mix_rounds: u64,
    pub mixed_peer_frames: u64,
    pub mix_nonzero_rounds: u64,
    pub mix_silent_rounds: u64,
    pub mix_samples: u64,
    pub mix_nonzero_samples: u64,
    pub mix_peak: f32,
    pub mix_rms: f64,
    pub jitter_idle_ticks: u64,
    pub dsp_config_generation: u64,
    pub dsp_requested_aec: bool,
    pub dsp_requested_agc: bool,
    pub dsp_requested_ns: bool,
    pub dsp_requested_ns_very_high: bool,
    pub dsp_requested_hpf: bool,
    pub dsp_apm_loaded: bool,
    pub dsp_config_fully_applied: bool,
    pub dsp_applied_aec: bool,
    pub dsp_applied_agc: bool,
    pub dsp_applied_ns: bool,
    pub dsp_applied_ns_very_high: bool,
    pub dsp_applied_hpf: bool,
    pub input_gain: f32,
    pub input_vad_threshold: f32,
    pub input_noise_gate_threshold: f32,
    pub media_receive: MediaReceiveStats,
    pub network_paths: Vec<NetworkPathStats>,
    pub encoder_packet_loss_percent: u64,
    pub encoder_bitrate: u64,
    pub aec_delay_ms: u64,
    pub aec_recommended_delay_ms: u64,
    pub aec_measured_delay_ms: u64,
    pub aec_input_latency_ms: u64,
    pub aec_output_latency_ms: u64,
    pub aec_render_queue_ms: u64,
    pub aec_capture_processing_ms: u64,
    pub aec_capture_path_ms: u64,
    pub aec_timing_complete: bool,
    pub aec_input_timing_present: bool,
    pub aec_output_timing_present: bool,
    pub aec_render_timing_present: bool,
    pub aec_capture_path_present: bool,
    pub aec_fallback_reason: String,
    pub aec_frame_timestamp_valid: bool,
    pub aec_render_observations: u64,
    pub aec_invalid_timestamp_samples: u64,
    pub aec_invalid_frame_timestamp_samples: u64,
    pub aec_last_frame_processed_present: bool,
    pub aec_last_frame_processed_age_ms: u64,
    pub aec_delay_frames: u64,
    pub game_state_updates: u64,
    pub applied_deaf: bool,
    pub applied_master: f32,
    pub applied_peer_count: u64,
    pub applied_nonzero_gain_peers: u64,
    pub playback_queued_pairs: u64,
    pub playback_spawn_attempts: u64,
    pub playback_starts: u64,
    pub playback_stops: u64,
    pub playback_errors: u64,
    pub playback_callback_errors: u64,
    pub playback_callbacks: u64,
    pub playback_requested_pairs: u64,
    pub playback_consumed_pairs: u64,
    pub playback_underrun_pairs: u64,
    pub playback_lock_contention_callbacks: u64,
    pub playback_lock_contention_silence_pairs: u64,
    pub playback_output_nonzero_samples: u64,
    pub playback_output_peak: f32,
    pub capture_ring_dropped: u64,
    pub playback_ring_len: u64,
    pub playback_ring_dropped: u64,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
#[serde(default)]
pub struct MediaReceiveStats {
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

#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
#[serde(default)]
pub struct NetworkPathStats {
    pub peer_id: String,
    pub generation: u32,
    pub candidate_pair_id: String,
    pub candidate_state: String,
    pub local_candidate_type: String,
    pub remote_candidate_type: String,
    pub relay: bool,
    pub current_rtt_ms: f64,
    pub bandwidth_estimate_valid: bool,
    pub available_outgoing_bitrate: f64,
    pub available_incoming_bitrate: f64,
    pub remote_packets_received: u64,
    pub remote_packets_lost: i64,
    pub remote_fraction_lost: f64,
    pub remote_report_rtt_ms: f64,
    pub remote_rtt_measurements: u64,
}

#[derive(Serialize)]
struct StatsMsg<'a> {
    op: &'static str,
    #[serde(flatten)]
    stats: &'a NativeStatsSnapshot,
}

#[derive(Serialize)]
struct ErrorMsg<'a> {
    op: &'static str,
    code: &'a str,
    msg: &'a str,
}

#[derive(Serialize)]
struct PongMsg {
    op: &'static str,
    #[serde(rename = "capTs")]
    cap_ts: u64,
}

pub fn ready_json(devices: &[DeviceInfo], output_devices: &[DeviceInfo]) -> String {
    serde_json::to_string(&ReadyMsg {
        op: "ready",
        proto: PROTO_VERSION,
        format: FormatBlock {
            rate: SAMPLE_RATE,
            channels: CHANNELS,
            sample: "f32",
        },
        devices,
        output_devices,
    })
    .expect("ready serialize")
}

pub fn devices_json(devices: &[DeviceInfo], output_devices: &[DeviceInfo]) -> String {
    serde_json::to_string(&DevicesMsg {
        op: "devices",
        devices,
        output_devices,
    })
    .expect("devices serialize")
}

pub fn level_json(peak: f32, speaking: bool) -> String {
    serde_json::to_string(&LevelMsg {
        op: "level",
        peak,
        speaking,
    })
    .expect("level serialize")
}

pub fn peer_levels_json(levels: &[PeerLevel]) -> String {
    serde_json::to_string(&PeerLevelsMsg {
        op: "peer-levels",
        levels,
    })
    .expect("peer-levels serialize")
}

pub fn stats_json(stats: &NativeStatsSnapshot) -> String {
    serde_json::to_string(&StatsMsg { op: "stats", stats }).expect("stats serialize")
}

#[derive(Serialize)]
struct StatsWithDiagnosticsMsg<'a, T: Serialize> {
    op: &'static str,
    #[serde(flatten)]
    stats: &'a NativeStatsSnapshot,
    diagnostics: &'a T,
}

pub fn stats_json_with_diagnostics<T: Serialize>(
    stats: &NativeStatsSnapshot,
    diagnostics: &T,
) -> String {
    serde_json::to_string(&StatsWithDiagnosticsMsg {
        op: "stats",
        stats,
        diagnostics,
    })
    .expect("stats diagnostics serialize")
}

pub fn error_json(code: &str, msg: &str) -> String {
    serde_json::to_string(&ErrorMsg {
        op: "error",
        code,
        msg,
    })
    .expect("error serialize")
}

pub fn pong_json(cap_ts: u64) -> String {
    serde_json::to_string(&PongMsg { op: "pong", cap_ts }).expect("pong serialize")
}

#[derive(Serialize)]
struct LocalSdpMsg<'a> {
    op: &'static str,
    peer_id: &'a str,
    generation: u32,
    sdp_type: &'a str,
    sdp: &'a str,
}

#[derive(Serialize)]
struct LocalCandidateMsg<'a> {
    op: &'static str,
    peer_id: &'a str,
    generation: u32,
    candidate: &'a str,
}

#[derive(Serialize)]
struct PeerStateMsg<'a> {
    op: &'static str,
    peer_id: &'a str,
    generation: u32,
    state: &'a str,
}

pub fn local_sdp_json(peer_id: &str, generation: u32, sdp_type: &str, sdp: &str) -> String {
    serde_json::to_string(&LocalSdpMsg {
        op: "local-sdp",
        peer_id,
        generation,
        sdp_type,
        sdp,
    })
    .expect("local-sdp serialize")
}

pub fn local_candidate_json(peer_id: &str, generation: u32, candidate: &str) -> String {
    serde_json::to_string(&LocalCandidateMsg {
        op: "local-candidate",
        peer_id,
        generation,
        candidate,
    })
    .expect("local-candidate serialize")
}

pub fn peer_state_json(peer_id: &str, generation: u32, state: &str) -> String {
    serde_json::to_string(&PeerStateMsg {
        op: "peer-state",
        peer_id,
        generation,
        state,
    })
    .expect("peer-state serialize")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frozen_constants_match_contract() {
        assert_eq!(PROTO_VERSION, 12);
        assert_eq!(SAMPLE_RATE, 48_000);
        assert_eq!(CHANNELS, 1);
        assert_eq!(FRAME_SAMPLES, 960);
        assert_eq!(FRAME_BYTES, 8 + 960 * 4);
        assert_eq!(TYPE_CONTROL, 0x01);
        assert_eq!(TYPE_AUDIO, 0x02);
        assert_eq!(TYPE_AUDIO_OUT, 0x03);
        assert_eq!(AUDIO_OUT_BYTES, 1920 * 4);
    }

    #[test]
    fn parse_set_dsp() {
        let op = parse_inbound(
            r#"{"op":"set-dsp","aec":true,"agc":false,"ns":true,"ns_very_high":true,"hpf":false}"#,
        )
        .unwrap();
        match op {
            InboundOp::SetDsp {
                aec,
                agc,
                ns,
                ns_very_high,
                hpf,
            } => {
                assert!(aec);
                assert!(!agc);
                assert!(ns);
                assert!(ns_very_high);
                assert!(!hpf);
            }
            other => panic!("expected set-dsp, got {other:?}"),
        }
    }

    #[test]
    fn parse_select_output_device() {
        let op = parse_inbound(r#"{"op":"select-output-device","id":"spk-1"}"#).unwrap();
        match op {
            InboundOp::SelectOutputDevice { id } => assert_eq!(id, "spk-1"),
            other => panic!("expected select-output-device, got {other:?}"),
        }
    }

    #[test]
    fn parse_peer_and_capture_ops() {
        match parse_inbound(r#"{"op":"set-diagnostics","enabled":false}"#).unwrap() {
            InboundOp::SetDiagnostics { enabled } => assert!(!enabled),
            other => panic!("expected set-diagnostics, got {other:?}"),
        }
        match parse_inbound(r#"{"op":"set-synthetic","enabled":true}"#).unwrap() {
            InboundOp::SetSynthetic { enabled } => assert!(enabled),
            other => panic!("expected set-synthetic, got {other:?}"),
        }
        match parse_inbound(
            r#"{"op":"set-input","gain":1.25,"vad_threshold":0.006,"noise_gate_threshold":0.003}"#,
        )
        .unwrap()
        {
            InboundOp::SetInput {
                gain,
                vad_threshold,
                noise_gate_threshold,
            } => {
                assert_eq!(gain, 1.25);
                assert_eq!(vad_threshold, 0.006);
                assert_eq!(noise_gate_threshold, 0.003);
            }
            other => panic!("expected set-input, got {other:?}"),
        }
        match parse_inbound(r#"{"op":"peer-add","peer_id":"p1","generation":41}"#).unwrap() {
            InboundOp::PeerAdd {
                peer_id,
                offerer,
                relay_only,
                generation,
            } => {
                assert_eq!(peer_id, "p1");
                assert!(offerer);
                assert!(!relay_only);
                assert_eq!(generation, 41);
            }
            other => panic!("expected peer-add, got {other:?}"),
        }
        match parse_inbound(
            r#"{"op":"peer-add","peer_id":"p1b","offerer":false,"relay_only":true,"generation":42}"#,
        )
        .unwrap()
        {
            InboundOp::PeerAdd {
                peer_id,
                offerer,
                relay_only,
                generation,
            } => {
                assert_eq!(peer_id, "p1b");
                assert!(!offerer);
                assert!(relay_only);
                assert_eq!(generation, 42);
            }
            other => panic!("expected peer-add, got {other:?}"),
        }
        assert!(parse_inbound(r#"{"op":"peer-add","peer_id":"missing-generation"}"#).is_err());
        match parse_inbound(r#"{"op":"peer-remove","peer_id":"p2"}"#).unwrap() {
            InboundOp::PeerRemove { peer_id } => assert_eq!(peer_id, "p2"),
            other => panic!("expected peer-remove, got {other:?}"),
        }
        match parse_inbound(
            r#"{"op":"set-remote-sdp","peer_id":"p3","sdp_type":"offer","sdp":"v=0"}"#,
        )
        .unwrap()
        {
            InboundOp::SetRemoteSdp {
                peer_id,
                sdp_type,
                sdp,
            } => {
                assert_eq!(peer_id, "p3");
                assert_eq!(sdp_type, "offer");
                assert_eq!(sdp, "v=0");
            }
            other => panic!("expected set-remote-sdp, got {other:?}"),
        }
        match parse_inbound(r#"{"op":"add-ice-candidate","peer_id":"p4","candidate":"c"}"#).unwrap()
        {
            InboundOp::AddIceCandidate { peer_id, candidate } => {
                assert_eq!(peer_id, "p4");
                assert_eq!(candidate, "c");
            }
            other => panic!("expected add-ice-candidate, got {other:?}"),
        }
        match parse_inbound(
            r#"{"op":"restart-ice","peer_id":"p5","relay_only":true,"create_offer":false}"#,
        )
        .unwrap()
        {
            InboundOp::RestartIce {
                peer_id,
                relay_only,
                create_offer,
            } => {
                assert_eq!(peer_id, "p5");
                assert!(relay_only);
                assert!(!create_offer);
            }
            other => panic!("expected restart-ice, got {other:?}"),
        }
        match parse_inbound(r#"{"op":"restart-ice","peer_id":"p6"}"#).unwrap() {
            InboundOp::RestartIce {
                relay_only,
                create_offer,
                ..
            } => {
                assert!(!relay_only);
                assert!(create_offer);
            }
            other => panic!("expected restart-ice defaults, got {other:?}"),
        }
    }

    #[test]
    fn parse_set_ice_servers() {
        let json = r#"{"op":"set-ice-servers","servers":[{"urls":["stun:stun.l.google.com:19302"]},{"urls":["turn:turn.example.com:3478"],"username":"u","credential":"c"}]}"#;
        match parse_inbound(json).unwrap() {
            InboundOp::SetIceServers { servers } => {
                assert_eq!(servers.len(), 2);
                assert_eq!(servers[0].urls, vec!["stun:stun.l.google.com:19302"]);
                assert!(servers[0].username.is_none());
                assert!(servers[0].credential.is_none());
                assert_eq!(servers[1].urls, vec!["turn:turn.example.com:3478"]);
                assert_eq!(servers[1].username.as_deref(), Some("u"));
                assert_eq!(servers[1].credential.as_deref(), Some("c"));
            }
            other => panic!("expected set-ice-servers, got {other:?}"),
        }
    }

    #[test]
    fn parse_game_state_op() {
        let json = r#"{"op":"game-state","deaf":false,"master":1.0,"peers":[{"id":"sock1","gain":1.0,"pan":0.0,"mode":0},{"id":"sock2","gain":0.5,"pan":-0.7,"mode":2}]}"#;
        match parse_inbound(json).unwrap() {
            InboundOp::GameState {
                deaf,
                master,
                peers,
            } => {
                assert!(!deaf);
                assert_eq!(master, 1.0);
                assert_eq!(peers.len(), 2);
                assert_eq!(peers[0].id, "sock1");
                assert_eq!(peers[0].gain, 1.0);
                assert_eq!(peers[1].id, "sock2");
                assert_eq!(peers[1].gain, 0.5);
                assert_eq!(peers[1].pan, -0.7);
                assert_eq!(peers[1].mode, 2);
            }
            other => panic!("expected game-state, got {other:?}"),
        }
    }

    #[test]
    fn parse_game_state_peer_defaults() {
        let json = r#"{"op":"game-state","deaf":true,"master":1.0,"peers":[{"id":"s"}]}"#;
        match parse_inbound(json).unwrap() {
            InboundOp::GameState { peers, deaf, .. } => {
                assert!(deaf);
                assert_eq!(peers[0].gain, 0.0);
                assert_eq!(peers[0].pan, 0.0);
                assert_eq!(peers[0].mode, 0);
            }
            other => panic!("expected game-state, got {other:?}"),
        }
    }

    #[test]
    fn local_signal_json_shapes() {
        let sv: serde_json::Value =
            serde_json::from_str(&local_sdp_json("p1", 41, "answer", "v=0")).unwrap();
        assert_eq!(sv["op"], "local-sdp");
        assert_eq!(sv["peer_id"], "p1");
        assert_eq!(sv["generation"], 41);
        assert_eq!(sv["sdp_type"], "answer");
        assert_eq!(sv["sdp"], "v=0");

        let cv: serde_json::Value =
            serde_json::from_str(&local_candidate_json("p1", 41, "cand")).unwrap();
        assert_eq!(cv["op"], "local-candidate");
        assert_eq!(cv["peer_id"], "p1");
        assert_eq!(cv["generation"], 41);
        assert_eq!(cv["candidate"], "cand");

        let pv: serde_json::Value =
            serde_json::from_str(&peer_state_json("p1", 41, "connected")).unwrap();
        assert_eq!(pv["op"], "peer-state");
        assert_eq!(pv["peer_id"], "p1");
        assert_eq!(pv["generation"], 41);
        assert_eq!(pv["state"], "connected");
    }

    #[test]
    fn native_stats_json_is_flat_and_contains_no_media_payloads() {
        let stats = NativeStatsSnapshot {
            capture_frames: 10,
            opus_encoded: 9,
            rtp_tx_ok: 18,
            rtp_rx_packets: 7,
            decode_frames: 6,
            mix_rounds: 5,
            mix_nonzero_rounds: 4,
            mix_silent_rounds: 1,
            mix_samples: 9_600,
            mix_nonzero_samples: 8_000,
            mix_peak: 0.5,
            mix_rms: 0.125,
            dsp_config_generation: 3,
            dsp_requested_aec: true,
            dsp_requested_agc: true,
            dsp_requested_ns: true,
            dsp_requested_ns_very_high: true,
            dsp_requested_hpf: false,
            dsp_apm_loaded: true,
            dsp_config_fully_applied: false,
            dsp_applied_aec: true,
            dsp_applied_agc: false,
            dsp_applied_ns: true,
            dsp_applied_ns_very_high: true,
            dsp_applied_hpf: false,
            input_gain: 1.25,
            input_vad_threshold: 0.006,
            aec_delay_ms: 87,
            aec_measured_delay_ms: 89,
            aec_input_latency_ms: 12,
            aec_output_latency_ms: 18,
            aec_render_queue_ms: 52,
            aec_capture_processing_ms: 4,
            aec_timing_complete: true,
            aec_input_timing_present: true,
            aec_output_timing_present: true,
            aec_render_timing_present: true,
            aec_capture_path_present: true,
            aec_last_frame_processed_present: true,
            aec_fallback_reason: "complete".to_string(),
            aec_render_observations: 100,
            aec_delay_frames: 200,
            game_state_updates: 20,
            applied_deaf: false,
            applied_master: 0.75,
            applied_peer_count: 2,
            applied_nonzero_gain_peers: 1,
            playback_queued_pairs: 4_800,
            playback_callbacks: 10,
            playback_consumed_pairs: 4_700,
            playback_underrun_pairs: 100,
            playback_output_nonzero_samples: 8_000,
            playback_output_peak: 0.4,
            ..Default::default()
        };
        let value: serde_json::Value = serde_json::from_str(&stats_json(&stats)).unwrap();
        assert_eq!(value["op"], "stats");
        assert_eq!(value["capture_frames"], 10);
        assert_eq!(value["opus_encoded"], 9);
        assert_eq!(value["rtp_tx_ok"], 18);
        assert_eq!(value["rtp_rx_packets"], 7);
        assert_eq!(value["decode_frames"], 6);
        assert_eq!(value["mix_rounds"], 5);
        assert_eq!(value["mix_nonzero_rounds"], 4);
        assert_eq!(value["mix_silent_rounds"], 1);
        assert_eq!(value["mix_peak"], 0.5);
        assert_eq!(value["mix_rms"], 0.125);
        assert_eq!(value["dsp_config_generation"], 3);
        assert_eq!(value["dsp_requested_aec"], true);
        assert_eq!(value["dsp_requested_agc"], true);
        assert_eq!(value["dsp_requested_ns"], true);
        assert_eq!(value["dsp_requested_ns_very_high"], true);
        assert_eq!(value["dsp_requested_hpf"], false);
        assert_eq!(value["dsp_apm_loaded"], true);
        assert_eq!(value["dsp_config_fully_applied"], false);
        assert_eq!(value["dsp_applied_aec"], true);
        assert_eq!(value["dsp_applied_agc"], false);
        assert_eq!(value["dsp_applied_ns"], true);
        assert_eq!(value["dsp_applied_ns_very_high"], true);
        assert_eq!(value["dsp_applied_hpf"], false);
        assert_eq!(value["input_gain"], 1.25);
        assert_eq!(value["input_vad_threshold"], 0.006);
        assert_eq!(value["aec_delay_ms"], 87);
        assert_eq!(value["aec_measured_delay_ms"], 89);
        assert_eq!(value["aec_input_latency_ms"], 12);
        assert_eq!(value["aec_output_latency_ms"], 18);
        assert_eq!(value["aec_render_queue_ms"], 52);
        assert_eq!(value["aec_capture_processing_ms"], 4);
        assert_eq!(value["aec_timing_complete"], true);
        assert_eq!(value["aec_input_timing_present"], true);
        assert_eq!(value["aec_output_timing_present"], true);
        assert_eq!(value["aec_render_timing_present"], true);
        assert_eq!(value["aec_capture_path_present"], true);
        assert_eq!(value["aec_last_frame_processed_present"], true);
        assert_eq!(value["aec_fallback_reason"], "complete");
        assert_eq!(value["aec_render_observations"], 100);
        assert_eq!(value["aec_delay_frames"], 200);
        assert_eq!(value["game_state_updates"], 20);
        assert_eq!(value["applied_deaf"], false);
        assert_eq!(value["applied_master"], 0.75);
        assert_eq!(value["applied_peer_count"], 2);
        assert_eq!(value["applied_nonzero_gain_peers"], 1);
        assert_eq!(value["playback_queued_pairs"], 4_800);
        assert_eq!(value["playback_callbacks"], 10);
        assert_eq!(value["playback_consumed_pairs"], 4_700);
        assert_eq!(value["playback_underrun_pairs"], 100);
        assert_eq!(value["playback_output_nonzero_samples"], 8_000);
        assert_eq!(value["playback_output_peak"], 0.4);
        assert!(value.get("sdp").is_none());
        assert!(value.get("candidate").is_none());
    }

    #[test]
    fn native_stats_diagnostics_are_additive_and_nested() {
        let stats = NativeStatsSnapshot {
            capture_frames: 4,
            ..Default::default()
        };
        let diagnostics = serde_json::json!({
            "schema": 1,
            "capture": { "stream_generation": 2, "running": true }
        });
        let value: serde_json::Value =
            serde_json::from_str(&stats_json_with_diagnostics(&stats, &diagnostics)).unwrap();
        assert_eq!(value["op"], "stats");
        assert_eq!(value["capture_frames"], 4);
        assert_eq!(value["diagnostics"]["schema"], 1);
        assert_eq!(value["diagnostics"]["capture"]["stream_generation"], 2);
    }

    #[test]
    fn native_stats_deserialize_defaults_additive_fields_from_old_payloads() {
        let stats: NativeStatsSnapshot = serde_json::from_str(r#"{"capture_frames":7}"#).unwrap();
        assert_eq!(stats.capture_frames, 7);
        assert_eq!(stats.aec_capture_path_ms, 0);
        assert!(!stats.aec_timing_complete);
        assert!(stats.aec_fallback_reason.is_empty());
    }

    #[test]
    fn playback_ring_drops_oldest_when_full() {
        let mut ring = PlaybackRing::new(2);
        ring.push(&[1.0, 1.0, 2.0, 2.0, 3.0, 3.0]);
        assert_eq!(ring.len(), 2);
        assert_eq!(ring.dropped(), 1);
        assert_eq!(ring.pop_stereo(), Some((2.0, 2.0)));
        assert_eq!(ring.pop_stereo(), Some((3.0, 3.0)));
        assert_eq!(ring.pop_stereo(), None);
    }

    #[test]
    fn playback_consumer_reads_without_borrowing_control_path_ring() {
        let mut ring = PlaybackRing::new(4);
        let consumer = ring.consumer();
        ring.push(&[1.0, -1.0, 2.0, -2.0]);
        assert_eq!(consumer.len(), 2);
        assert_eq!(consumer.pop_stereo(), Some((1.0, -1.0)));
        ring.push(&[3.0, -3.0]);
        assert_eq!(consumer.pop_stereo(), Some((2.0, -2.0)));
        assert_eq!(consumer.pop_stereo(), Some((3.0, -3.0)));
        assert_eq!(consumer.pop_stereo(), None);
    }

    #[test]
    fn playback_spsc_never_returns_torn_or_reordered_pairs_under_overflow() {
        use std::sync::atomic::{AtomicBool, Ordering};

        let mut ring = PlaybackRing::new(32);
        for sequence in 1..=64u32 {
            let value = sequence as f32;
            ring.push(&[value, -value]);
        }
        let consumer = ring.consumer();
        let complete = Arc::new(AtomicBool::new(false));
        let producer_complete = complete.clone();
        let producer = std::thread::spawn(move || {
            for sequence in 65..=20_000u32 {
                let value = sequence as f32;
                ring.push(&[value, -value]);
            }
            producer_complete.store(true, Ordering::Release);
            ring
        });

        let mut previous = 0.0f32;
        while !complete.load(Ordering::Acquire) || consumer.len() > 0 {
            if let Some((left, right)) = consumer.pop_stereo() {
                assert_eq!(left, -right, "stereo pair was torn");
                assert!(
                    left > previous,
                    "SPSC output reordered {left} after {previous}"
                );
                previous = left;
            } else {
                std::thread::yield_now();
            }
        }
        let ring = producer.join().unwrap();
        assert_eq!(previous, 20_000.0);
        assert!(ring.dropped() > 0, "stress case should exercise overflow");
    }

    #[test]
    fn control_frame_roundtrips() {
        let json = r#"{"op":"hello","proto":1,"token":"abc"}"#;
        let bytes = encode_control(json);
        assert_eq!(bytes[0], TYPE_CONTROL);
        let len = u32::from_le_bytes([bytes[1], bytes[2], bytes[3], bytes[4]]) as usize;
        assert_eq!(len, json.len());
        let mut cursor = std::io::Cursor::new(bytes);
        match read_frame(&mut cursor).unwrap() {
            Frame::Control(s) => assert_eq!(s, json),
            other => panic!("expected control, got {:?}", other),
        }
    }

    #[test]
    fn audio_frame_roundtrips_with_exact_layout() {
        let mut samples = vec![0.0f32; FRAME_SAMPLES];
        samples[0] = 1.0;
        samples[FRAME_SAMPLES - 1] = -0.5;
        let frame = AudioFrame {
            encoder_epoch: 0,
            capture_generation: 0,
            capture_open_attempt: 0,
            capture_ts_ns: 0x0102_0304_0506_0708,
            capture_callback_ts_ns: 0,
            capture_timestamp_valid: false,
            samples,
        };
        let bytes = encode_audio(&frame);
        assert_eq!(bytes[0], TYPE_AUDIO);
        let len = u32::from_le_bytes([bytes[1], bytes[2], bytes[3], bytes[4]]) as usize;
        assert_eq!(len, FRAME_BYTES);
        assert_eq!(bytes.len(), 5 + FRAME_BYTES);
        assert_eq!(&bytes[5..13], &0x0102_0304_0506_0708u64.to_le_bytes());
        let mut cursor = std::io::Cursor::new(bytes);
        match read_frame(&mut cursor).unwrap() {
            Frame::Audio(f) => {
                assert_eq!(f.capture_ts_ns, 0x0102_0304_0506_0708);
                assert_eq!(f.samples.len(), FRAME_SAMPLES);
                assert_eq!(f.samples[0], 1.0);
                assert_eq!(f.samples[FRAME_SAMPLES - 1], -0.5);
            }
            other => panic!("expected audio, got {:?}", other),
        }
    }

    #[test]
    fn read_frame_rejects_unknown_type() {
        let buf = vec![0x09u8, 0, 0, 0, 0];
        let mut cursor = std::io::Cursor::new(buf);
        match read_frame(&mut cursor) {
            Err(DecodeError::BadType(0x09)) => {}
            other => panic!("expected BadType(9), got {:?}", other),
        }
    }

    #[test]
    fn read_frame_rejects_wrong_audio_len() {
        let mut buf = vec![TYPE_AUDIO];
        buf.extend_from_slice(&(7u32).to_le_bytes());
        buf.extend_from_slice(&[0u8; 7]);
        let mut cursor = std::io::Cursor::new(buf);
        match read_frame(&mut cursor) {
            Err(DecodeError::BadLen(7)) => {}
            other => panic!("expected BadLen(7), got {:?}", other),
        }
    }

    fn push_capture(producer: &CaptureFrameProducer, ts: u64, value: f32) {
        assert!(producer.push(
            CaptureFrameMetadata {
                encoder_epoch: 7,
                capture_generation: 1,
                capture_open_attempt: 2,
                capture_ts_ns: ts,
                capture_callback_ts_ns: ts + 10,
                capture_timestamp_valid: true,
            },
            &[value; FRAME_SAMPLES],
        ));
    }

    fn reusable_capture_frame() -> AudioFrame {
        AudioFrame {
            encoder_epoch: 0,
            capture_generation: 0,
            capture_open_attempt: 0,
            capture_ts_ns: 0,
            capture_callback_ts_ns: 0,
            capture_timestamp_valid: false,
            samples: vec![0.0; FRAME_SAMPLES],
        }
    }

    #[test]
    fn ring_is_fifo_when_not_full() {
        let (producer, consumer) = capture_frame_ring(4);
        push_capture(&producer, 1, 0.1);
        push_capture(&producer, 2, 0.2);
        push_capture(&producer, 3, 0.3);
        assert_eq!(consumer.len(), 3);
        assert_eq!(consumer.dropped(), 0);
        assert_eq!(consumer.oldest_capture_ts_ns(), Some(1));
        let mut frame = reusable_capture_frame();
        for expected in [1, 2, 3] {
            assert!(consumer.pop_into(&mut frame));
            assert_eq!(frame.capture_ts_ns, expected);
        }
        assert!(!consumer.pop_into(&mut frame));
    }

    #[test]
    fn ring_drops_oldest_and_counts_when_full() {
        let (producer, consumer) = capture_frame_ring(3);
        for ts in 1..=5 {
            push_capture(&producer, ts, ts as f32);
        }
        assert_eq!(consumer.len(), 3);
        assert_eq!(consumer.dropped(), 2);
        assert_eq!(consumer.oldest_capture_ts_ns(), Some(3));
        let mut frame = reusable_capture_frame();
        for expected in [3, 4, 5] {
            assert!(consumer.pop_into(&mut frame));
            assert_eq!(frame.capture_ts_ns, expected);
            assert!(frame
                .samples
                .iter()
                .all(|sample| *sample == expected as f32));
        }
        assert!(!consumer.pop_into(&mut frame));
    }

    #[test]
    fn ring_discard_reset_removes_stale_frames_and_reuses_storage() {
        let (producer, consumer) = capture_frame_ring(3);
        push_capture(&producer, 1, 1.0);
        push_capture(&producer, 2, 2.0);
        assert_eq!(consumer.discard_all(), 2);
        assert_eq!(consumer.len(), 0);

        let mut frame = reusable_capture_frame();
        let pointer = frame.samples.as_ptr();
        let capacity = frame.samples.capacity();
        for ts in 3..100 {
            push_capture(&producer, ts, ts as f32);
            assert!(consumer.pop_into(&mut frame));
            assert_eq!(frame.samples.as_ptr(), pointer);
            assert_eq!(frame.samples.capacity(), capacity);
        }
        assert_eq!(consumer.reset(), 0);
    }

    #[test]
    fn ring_concurrent_overflow_never_returns_torn_frames() {
        let (producer, consumer) = capture_frame_ring(8);
        let done = Arc::new(std::sync::atomic::AtomicBool::new(false));
        let producer_done = done.clone();
        let writer = std::thread::spawn(move || {
            for sequence in 1..=20_000u64 {
                push_capture(&producer, sequence, sequence as f32);
            }
            producer_done.store(true, Ordering::Release);
        });

        let mut frame = reusable_capture_frame();
        let mut last = 0;
        while !done.load(Ordering::Acquire) || consumer.len() > 0 {
            if consumer.pop_into(&mut frame) {
                assert!(frame.capture_ts_ns > last);
                assert!(frame
                    .samples
                    .iter()
                    .all(|sample| *sample == frame.capture_ts_ns as f32));
                last = frame.capture_ts_ns;
            } else {
                std::thread::yield_now();
            }
        }
        writer.join().unwrap();
        assert!(last > 0);
    }

    #[test]
    fn ring_capacity_constant_is_tiny() {
        assert_eq!(RING_CAPACITY, 8);
    }

    #[test]
    fn parse_hello() {
        let op = parse_inbound(r#"{"op":"hello","proto":1,"token":"secret"}"#).unwrap();
        match op {
            InboundOp::Hello { proto, token } => {
                assert_eq!(proto, 1);
                assert_eq!(token, "secret");
            }
            other => panic!("expected hello, got {other:?}"),
        }
    }

    #[test]
    fn parse_select_device_with_hyphenated_op() {
        let op = parse_inbound(r#"{"op":"select-device","id":"dev-7"}"#).unwrap();
        match op {
            InboundOp::SelectDevice { id } => assert_eq!(id, "dev-7"),
            other => panic!("expected select-device, got {other:?}"),
        }
    }

    #[test]
    fn parse_start_stop_ping() {
        assert!(matches!(
            parse_inbound(r#"{"op":"start"}"#).unwrap(),
            InboundOp::Start
        ));
        assert!(matches!(
            parse_inbound(r#"{"op":"stop"}"#).unwrap(),
            InboundOp::Stop
        ));
        assert!(matches!(
            parse_inbound(r#"{"op":"ping"}"#).unwrap(),
            InboundOp::Ping
        ));
    }

    #[test]
    fn parse_rejects_unknown_op() {
        assert!(parse_inbound(r#"{"op":"frobnicate"}"#).is_err());
    }

    #[test]
    fn ready_json_has_exact_format_block() {
        let devs = vec![DeviceInfo {
            id: "a".into(),
            name: "Mic A".into(),
            default: true,
        }];
        let s = ready_json(&devs, &[]);
        let v: serde_json::Value = serde_json::from_str(&s).unwrap();
        assert_eq!(v["op"], "ready");
        assert_eq!(v["proto"], 12);
        assert_eq!(v["format"]["rate"], 48_000);
        assert_eq!(v["format"]["channels"], 1);
        assert_eq!(v["format"]["sample"], "f32");
        assert_eq!(v["devices"][0]["id"], "a");
        assert_eq!(v["devices"][0]["name"], "Mic A");
        assert_eq!(v["devices"][0]["default"], true);
    }

    #[test]
    fn devices_level_error_pong_json_shapes() {
        let devs = vec![DeviceInfo {
            id: "x".into(),
            name: "X".into(),
            default: false,
        }];
        let dv: serde_json::Value = serde_json::from_str(&devices_json(&devs, &[])).unwrap();
        assert_eq!(dv["op"], "devices");
        assert_eq!(dv["devices"][0]["id"], "x");

        let lv: serde_json::Value = serde_json::from_str(&level_json(0.5, true)).unwrap();
        assert_eq!(lv["op"], "level");
        assert!((lv["peak"].as_f64().unwrap() - 0.5).abs() < 1e-6);
        assert_eq!(lv["speaking"], true);

        let pv: serde_json::Value = serde_json::from_str(&peer_levels_json(&[
            PeerLevel {
                peer_id: "p1".into(),
                peak: 0.75,
            },
            PeerLevel {
                peer_id: "p2".into(),
                peak: 0.25,
            },
        ]))
        .unwrap();
        assert_eq!(pv["op"], "peer-levels");
        assert_eq!(pv["levels"][0]["peer_id"], "p1");
        assert_eq!(pv["levels"][0]["peak"], 0.75);

        let ev: serde_json::Value =
            serde_json::from_str(&error_json("mic-denied", "no mic")).unwrap();
        assert_eq!(ev["op"], "error");
        assert_eq!(ev["code"], "mic-denied");
        assert_eq!(ev["msg"], "no mic");

        let pv: serde_json::Value = serde_json::from_str(&pong_json(123456789)).unwrap();
        assert_eq!(pv["op"], "pong");
        assert_eq!(pv["capTs"], 123456789u64);
    }
}
