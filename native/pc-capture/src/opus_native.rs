//! Small, allocation-stable wrapper around PerfectComms' bundled libopus build.
//!
//! `opusic-c` deliberately exposes the raw libopus API. Keeping the unsafe surface here lets the
//! rest of the media engine use ordinary Rust slices while still reaching the Opus 1.6 DRED APIs
//! that are not represented by the older `audiopus` crate.

use opusic_c::sys;
use std::ffi::CStr;
use std::fmt;
use std::ptr::NonNull;

pub const SAMPLE_RATE: i32 = 48_000;
pub const CHANNELS: usize = 1;
pub const FRAME_SIZE: usize = 960;
pub const MAX_PACKET_BYTES: usize = 4_000;

/// Opus expresses DRED history in 10 ms units. The receiver only repairs gaps up to 100 ms, so a
/// matching 100 ms encoder window avoids spending redundancy bits on history it will never use.
pub const DEFAULT_DRED_DURATION_10MS: u8 = 10;

const DEFAULT_BITRATE: i32 = 48_000;
const MIN_BITRATE: i32 = 16_000;
const MAX_BITRATE: i32 = 64_000;
const DEFAULT_PACKET_LOSS_PERCENT: u8 = 15;
const MAX_DRED_DURATION_10MS: u8 = 104;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct EncoderConfig {
    pub bitrate: i32,
    pub complexity: u8,
    pub packet_loss_percent: u8,
    pub dred_duration_10ms: u8,
}

impl Default for EncoderConfig {
    fn default() -> Self {
        Self {
            bitrate: DEFAULT_BITRATE,
            complexity: 10,
            packet_loss_percent: DEFAULT_PACKET_LOSS_PERCENT,
            dred_duration_10ms: DEFAULT_DRED_DURATION_10MS,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DredCapability {
    Available,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DredInfo {
    /// How far before the current packet the oldest decoded DRED sample reaches.
    pub oldest_offset_samples: usize,
    /// Distance from the current packet to the newest DRED sample.
    pub end_silence_samples: usize,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DredParseOutcome {
    Available(DredInfo),
    NotPresent,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DredDecodeOutcome {
    Decoded { samples: usize, info: DredInfo },
    NotPresent,
    OffsetNotCovered { info: DredInfo },
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum OpusError {
    Codec { operation: &'static str, code: i32 },
    Allocation { operation: &'static str },
    InvalidPcmLength { expected: usize, actual: usize },
    OutputTooSmall { required: usize, actual: usize },
    EmptyPacket,
    LengthOverflow,
    InvalidConfig(&'static str),
}

impl fmt::Display for OpusError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Codec { operation, code } => {
                write!(f, "{operation} failed: {} ({code})", opus_error_text(*code))
            }
            Self::Allocation { operation } => write!(f, "{operation} allocation failed"),
            Self::InvalidPcmLength { expected, actual } => {
                write!(f, "expected {expected} PCM samples, got {actual}")
            }
            Self::OutputTooSmall { required, actual } => {
                write!(f, "output needs {required} samples, got {actual}")
            }
            Self::EmptyPacket => f.write_str("Opus packet is empty"),
            Self::LengthOverflow => f.write_str("buffer length exceeds the libopus i32 ABI"),
            Self::InvalidConfig(message) => write!(f, "invalid Opus configuration: {message}"),
        }
    }
}

impl std::error::Error for OpusError {}

fn opus_error_text(code: i32) -> &'static str {
    // SAFETY: libopus returns a process-lifetime static string for every error code.
    let ptr = unsafe { sys::opus_strerror(code) };
    if ptr.is_null() {
        return "unknown libopus error";
    }
    // SAFETY: the non-null pointer is a NUL-terminated process-lifetime libopus string.
    unsafe { CStr::from_ptr(ptr) }
        .to_str()
        .unwrap_or("non-UTF-8 libopus error")
}

fn checked_i32(value: usize) -> Result<i32, OpusError> {
    i32::try_from(value).map_err(|_| OpusError::LengthOverflow)
}

fn check(operation: &'static str, code: i32) -> Result<i32, OpusError> {
    if code < 0 {
        Err(OpusError::Codec { operation, code })
    } else {
        Ok(code)
    }
}

pub fn libopus_version() -> &'static str {
    // SAFETY: libopus returns a non-null, process-lifetime NUL-terminated version string.
    let ptr = unsafe { sys::opus_get_version_string() };
    if ptr.is_null() {
        return "unknown libopus version";
    }
    // SAFETY: see above.
    unsafe { CStr::from_ptr(ptr) }
        .to_str()
        .unwrap_or("non-UTF-8 libopus version")
}

pub struct OpusEncoder {
    inner: NonNull<sys::OpusEncoder>,
    packet: Box<[u8; MAX_PACKET_BYTES]>,
    dred_duration_10ms: u8,
}

impl OpusEncoder {
    pub fn new() -> Result<Self, OpusError> {
        Self::with_config(EncoderConfig::default())
    }

    pub fn with_config(config: EncoderConfig) -> Result<Self, OpusError> {
        if config.complexity > 10 {
            return Err(OpusError::InvalidConfig("complexity must be in 0..=10"));
        }
        if config.packet_loss_percent > 100 {
            return Err(OpusError::InvalidConfig(
                "packet loss percentage must be in 0..=100",
            ));
        }
        if config.dred_duration_10ms > MAX_DRED_DURATION_10MS {
            return Err(OpusError::InvalidConfig(
                "DRED duration must be in 0..=104 10 ms units",
            ));
        }
        if !(MIN_BITRATE..=MAX_BITRATE).contains(&config.bitrate) {
            return Err(OpusError::InvalidConfig(
                "bitrate must be in 16000..=64000 bits/s",
            ));
        }

        let mut error = sys::OPUS_OK;
        // SAFETY: all values are valid libopus constants and `error` is writable.
        let raw = unsafe {
            sys::opus_encoder_create(
                SAMPLE_RATE,
                CHANNELS as i32,
                sys::OPUS_APPLICATION_VOIP,
                &mut error,
            )
        };
        let inner = NonNull::new(raw).ok_or({
            if error < 0 {
                OpusError::Codec {
                    operation: "opus_encoder_create",
                    code: error,
                }
            } else {
                OpusError::Allocation {
                    operation: "opus_encoder_create",
                }
            }
        })?;

        let mut encoder = Self {
            inner,
            packet: Box::new([0; MAX_PACKET_BYTES]),
            dred_duration_10ms: 0,
        };
        encoder.set_ctl(sys::OPUS_SET_BITRATE_REQUEST, config.bitrate, "set bitrate")?;
        encoder.set_ctl(
            sys::OPUS_SET_COMPLEXITY_REQUEST,
            config.complexity as i32,
            "set complexity",
        )?;
        encoder.set_ctl(
            sys::OPUS_SET_SIGNAL_REQUEST,
            sys::OPUS_SIGNAL_VOICE,
            "set signal",
        )?;
        encoder.set_ctl(sys::OPUS_SET_VBR_REQUEST, 1, "enable VBR")?;
        encoder.set_ctl(
            sys::OPUS_SET_VBR_CONSTRAINT_REQUEST,
            1,
            "enable constrained VBR",
        )?;
        encoder.set_ctl(sys::OPUS_SET_DTX_REQUEST, 0, "disable DTX")?;
        // Keep classic one-packet FEC as the low-cost fallback for the newest missing frame. DRED
        // handles older loss; callers report the two recovery mechanisms separately.
        encoder.set_ctl(sys::OPUS_SET_INBAND_FEC_REQUEST, 1, "enable in-band FEC")?;
        encoder.set_ctl(
            sys::OPUS_SET_PACKET_LOSS_PERC_REQUEST,
            config.packet_loss_percent as i32,
            "set packet-loss expectation",
        )?;
        encoder.set_ctl(
            sys::OPUS_SET_DRED_DURATION_REQUEST,
            config.dred_duration_10ms as i32,
            "set DRED duration",
        )?;

        let actual = encoder.get_ctl(sys::OPUS_GET_DRED_DURATION_REQUEST, "get DRED duration")?;
        if actual != config.dred_duration_10ms as i32 {
            return Err(OpusError::InvalidConfig(
                "libopus did not retain the requested DRED duration",
            ));
        }
        encoder.dred_duration_10ms = actual as u8;
        Ok(encoder)
    }

    fn set_ctl(
        &mut self,
        request: i32,
        value: i32,
        operation: &'static str,
    ) -> Result<(), OpusError> {
        // SAFETY: `inner` is an owned live encoder. These requests all take one promoted i32.
        let result = unsafe { sys::opus_encoder_ctl(self.inner.as_ptr(), request, value) };
        check(operation, result).map(|_| ())
    }

    fn get_ctl(&mut self, request: i32, operation: &'static str) -> Result<i32, OpusError> {
        let mut value = 0i32;
        // SAFETY: `inner` is live and GET requests receive a writable i32 pointer.
        let result = unsafe { sys::opus_encoder_ctl(self.inner.as_ptr(), request, &mut value) };
        check(operation, result).map(|_| value)
    }

    /// Encodes exactly one mono 20 ms frame into a reused internal packet buffer.
    pub fn encode<'a>(&'a mut self, pcm: &[f32]) -> Result<&'a [u8], OpusError> {
        if pcm.len() != FRAME_SIZE {
            return Err(OpusError::InvalidPcmLength {
                expected: FRAME_SIZE,
                actual: pcm.len(),
            });
        }
        // SAFETY: `pcm` has exactly one legal frame and `packet` is writable for the declared cap.
        let result = unsafe {
            sys::opus_encode_float(
                self.inner.as_ptr(),
                pcm.as_ptr(),
                FRAME_SIZE as i32,
                self.packet.as_mut_ptr(),
                MAX_PACKET_BYTES as i32,
            )
        };
        let len = check("opus_encode_float", result)? as usize;
        Ok(&self.packet[..len])
    }

    pub fn encode_into(&mut self, pcm: &[f32], output: &mut [u8]) -> Result<usize, OpusError> {
        if pcm.len() != FRAME_SIZE {
            return Err(OpusError::InvalidPcmLength {
                expected: FRAME_SIZE,
                actual: pcm.len(),
            });
        }
        if output.is_empty() {
            return Err(OpusError::OutputTooSmall {
                required: 1,
                actual: 0,
            });
        }
        let max_len = checked_i32(output.len())?;
        // SAFETY: the validated slices remain live for the duration of the call.
        let result = unsafe {
            sys::opus_encode_float(
                self.inner.as_ptr(),
                pcm.as_ptr(),
                FRAME_SIZE as i32,
                output.as_mut_ptr(),
                max_len,
            )
        };
        check("opus_encode_float", result).map(|value| value as usize)
    }

    pub fn reset(&mut self) -> Result<(), OpusError> {
        let dred_duration_10ms = self.dred_duration_10ms;
        // SAFETY: reset has no variadic argument and `inner` is live.
        let result = unsafe { sys::opus_encoder_ctl(self.inner.as_ptr(), sys::OPUS_RESET_STATE) };
        check("reset encoder", result)?;

        // OPUS_RESET_STATE clears DRED's neural/history state (the privacy property this call is
        // used for) and also clears the DRED duration CTL because that field is inside libopus'
        // reset region. Restore only the configured duration; bitrate/FEC/VBR settings live in the
        // preserved configuration region.
        self.set_ctl(
            sys::OPUS_SET_DRED_DURATION_REQUEST,
            dred_duration_10ms as i32,
            "restore DRED duration after reset",
        )?;
        let actual = self.get_ctl(
            sys::OPUS_GET_DRED_DURATION_REQUEST,
            "verify DRED duration after reset",
        )?;
        if actual != dred_duration_10ms as i32 {
            return Err(OpusError::InvalidConfig(
                "libopus did not restore DRED duration after reset",
            ));
        }
        Ok(())
    }

    pub fn set_network_conditions(
        &mut self,
        packet_loss_percent: u8,
        bitrate: i32,
    ) -> Result<(), OpusError> {
        self.set_ctl(
            sys::OPUS_SET_PACKET_LOSS_PERC_REQUEST,
            packet_loss_percent.min(100) as i32,
            "set packet-loss expectation",
        )?;
        self.set_ctl(
            sys::OPUS_SET_BITRATE_REQUEST,
            bitrate.clamp(MIN_BITRATE, MAX_BITRATE),
            "set bitrate",
        )
    }

    pub fn dred_capability(&self) -> DredCapability {
        DredCapability::Available
    }

    pub fn dred_duration_10ms(&self) -> u8 {
        self.dred_duration_10ms
    }
}

impl Drop for OpusEncoder {
    fn drop(&mut self) {
        // SAFETY: this object uniquely owns the live encoder pointer.
        unsafe { sys::opus_encoder_destroy(self.inner.as_ptr()) };
    }
}

// libopus states may move between threads but must not be used concurrently. `&mut self` protects
// every stateful operation and the type intentionally does not implement Sync.
unsafe impl Send for OpusEncoder {}

struct DredResources {
    decoder: NonNull<sys::OpusDREDDecoder>,
    packet: NonNull<sys::OpusDRED>,
}

impl DredResources {
    fn new() -> Result<Self, OpusError> {
        let mut decoder_error = sys::OPUS_OK;
        // SAFETY: the error pointer is writable.
        let decoder_raw = unsafe { sys::opus_dred_decoder_create(&mut decoder_error) };
        let decoder = NonNull::new(decoder_raw).ok_or({
            if decoder_error < 0 {
                OpusError::Codec {
                    operation: "opus_dred_decoder_create",
                    code: decoder_error,
                }
            } else {
                OpusError::Allocation {
                    operation: "opus_dred_decoder_create",
                }
            }
        })?;

        let mut packet_error = sys::OPUS_OK;
        // SAFETY: the error pointer is writable.
        let packet_raw = unsafe { sys::opus_dred_alloc(&mut packet_error) };
        let packet = match NonNull::new(packet_raw) {
            Some(packet) => packet,
            None => {
                // SAFETY: allocation of the decoder succeeded and ownership has not escaped.
                unsafe { sys::opus_dred_decoder_destroy(decoder.as_ptr()) };
                return Err(if packet_error < 0 {
                    OpusError::Codec {
                        operation: "opus_dred_alloc",
                        code: packet_error,
                    }
                } else {
                    OpusError::Allocation {
                        operation: "opus_dred_alloc",
                    }
                });
            }
        };

        Ok(Self { decoder, packet })
    }

    fn reset(&mut self) -> Result<(), OpusError> {
        // Opus 1.6's DRED decoder does not implement OPUS_RESET_STATE (it returns
        // OPUS_UNIMPLEMENTED). Allocate the replacement before dropping the live resources so an
        // allocation failure leaves this decoder valid and the caller can fail closed.
        *self = Self::new()?;
        Ok(())
    }
}

impl Drop for DredResources {
    fn drop(&mut self) {
        // SAFETY: this object uniquely owns both live pointers.
        unsafe {
            sys::opus_dred_free(self.packet.as_ptr());
            sys::opus_dred_decoder_destroy(self.decoder.as_ptr());
        }
    }
}

pub struct OpusDecoder {
    inner: NonNull<sys::OpusDecoder>,
    dred: DredResources,
    parsed_dred: Option<DredInfo>,
}

impl OpusDecoder {
    pub fn new() -> Result<Self, OpusError> {
        let mut error = sys::OPUS_OK;
        // SAFETY: values are valid libopus parameters and `error` is writable.
        let raw = unsafe { sys::opus_decoder_create(SAMPLE_RATE, CHANNELS as i32, &mut error) };
        let inner = NonNull::new(raw).ok_or({
            if error < 0 {
                OpusError::Codec {
                    operation: "opus_decoder_create",
                    code: error,
                }
            } else {
                OpusError::Allocation {
                    operation: "opus_decoder_create",
                }
            }
        })?;

        let dred = match DredResources::new() {
            Ok(dred) => dred,
            Err(error) => {
                // SAFETY: decoder allocation succeeded and ownership has not escaped.
                unsafe { sys::opus_decoder_destroy(inner.as_ptr()) };
                return Err(error);
            }
        };
        Ok(Self {
            inner,
            dred,
            parsed_dred: None,
        })
    }

    fn validate_output(output: &[f32]) -> Result<(), OpusError> {
        if output.len() < FRAME_SIZE {
            Err(OpusError::OutputTooSmall {
                required: FRAME_SIZE,
                actual: output.len(),
            })
        } else {
            Ok(())
        }
    }

    fn decode_packet(
        &mut self,
        packet: Option<&[u8]>,
        output: &mut [f32],
        decode_fec: bool,
        operation: &'static str,
    ) -> Result<usize, OpusError> {
        Self::validate_output(output)?;
        if matches!(packet, Some(value) if value.is_empty()) {
            return Err(OpusError::EmptyPacket);
        }
        let (data, len) = match packet {
            Some(packet) => (packet.as_ptr(), checked_i32(packet.len())?),
            None => (std::ptr::null(), 0),
        };
        // SAFETY: pointers and lengths originate from validated live slices. A null, zero-length
        // packet is the documented PLC call. The output has room for one mono 20 ms frame.
        let result = unsafe {
            sys::opus_decode_float(
                self.inner.as_ptr(),
                data,
                len,
                output.as_mut_ptr(),
                FRAME_SIZE as i32,
                i32::from(decode_fec),
            )
        };
        check(operation, result).map(|value| value as usize)
    }

    pub fn decode(&mut self, packet: &[u8], output: &mut [f32]) -> Result<usize, OpusError> {
        self.decode_packet(Some(packet), output, false, "opus_decode_float")
    }

    pub fn decode_fec(
        &mut self,
        next_packet: &[u8],
        output: &mut [f32],
    ) -> Result<usize, OpusError> {
        self.decode_packet(Some(next_packet), output, true, "opus_decode_float (FEC)")
    }

    pub fn decode_plc(&mut self, output: &mut [f32]) -> Result<usize, OpusError> {
        self.decode_packet(None, output, false, "opus_decode_float (PLC)")
    }

    pub fn reset(&mut self) -> Result<(), OpusError> {
        // SAFETY: reset has no variadic argument and the primary decoder is live.
        let result = unsafe { sys::opus_decoder_ctl(self.inner.as_ptr(), sys::OPUS_RESET_STATE) };
        check("reset decoder", result)?;
        self.dred.reset()?;
        self.parsed_dred = None;
        Ok(())
    }

    pub fn dred_capability(&self) -> DredCapability {
        DredCapability::Available
    }

    pub fn parse_dred(
        &mut self,
        packet: &[u8],
        max_dred_samples: usize,
    ) -> Result<DredParseOutcome, OpusError> {
        if packet.is_empty() {
            return Err(OpusError::EmptyPacket);
        }
        let packet_len = checked_i32(packet.len())?;
        let max_dred_samples = checked_i32(max_dred_samples.min(SAMPLE_RATE as usize))?;
        if max_dred_samples <= 0 {
            self.parsed_dred = None;
            return Ok(DredParseOutcome::NotPresent);
        }
        let mut dred_end = 0i32;
        // SAFETY: all state pointers are uniquely owned and live; packet data remains live during
        // the call, and `dred_end` is writable. Processing is eager so decode can follow directly.
        let result = unsafe {
            sys::opus_dred_parse(
                self.dred.decoder.as_ptr(),
                self.dred.packet.as_ptr(),
                packet.as_ptr(),
                packet_len,
                max_dred_samples,
                SAMPLE_RATE,
                &mut dred_end,
                0,
            )
        };
        let oldest = check("opus_dred_parse", result)? as usize;
        if oldest == 0 {
            self.parsed_dred = None;
            return Ok(DredParseOutcome::NotPresent);
        }
        let info = DredInfo {
            oldest_offset_samples: oldest,
            end_silence_samples: dred_end.max(0) as usize,
        };
        self.parsed_dred = Some(info);
        Ok(DredParseOutcome::Available(info))
    }

    /// Decodes a previously parsed DRED frame at an explicit offset before the current packet.
    /// Call offsets in descending order (oldest frame first) to preserve decoder chronology.
    pub fn decode_parsed_dred(
        &mut self,
        offset_samples: usize,
        output: &mut [f32],
    ) -> Result<DredDecodeOutcome, OpusError> {
        Self::validate_output(output)?;
        let info = match self.parsed_dred {
            Some(info) => info,
            None => return Ok(DredDecodeOutcome::NotPresent),
        };
        if offset_samples == 0
            || offset_samples > info.oldest_offset_samples
            || offset_samples <= info.end_silence_samples
        {
            return Ok(DredDecodeOutcome::OffsetNotCovered { info });
        }
        let offset = checked_i32(offset_samples)?;
        // SAFETY: parse_dred initialized the owned DRED packet, output holds a legal frame, and the
        // explicit offset was checked against the coverage returned by libopus.
        let result = unsafe {
            sys::opus_decoder_dred_decode_float(
                self.inner.as_ptr(),
                self.dred.packet.as_ptr(),
                offset,
                output.as_mut_ptr(),
                FRAME_SIZE as i32,
            )
        };
        let samples = check("opus_decoder_dred_decode_float", result)? as usize;
        Ok(DredDecodeOutcome::Decoded { samples, info })
    }

    pub fn try_decode_dred(
        &mut self,
        packet: &[u8],
        max_dred_samples: usize,
        offset_samples: usize,
        output: &mut [f32],
    ) -> Result<DredDecodeOutcome, OpusError> {
        match self.parse_dred(packet, max_dred_samples)? {
            DredParseOutcome::Available(_) => self.decode_parsed_dred(offset_samples, output),
            DredParseOutcome::NotPresent => Ok(DredDecodeOutcome::NotPresent),
        }
    }
}

impl Drop for OpusDecoder {
    fn drop(&mut self) {
        // SAFETY: this object uniquely owns the live decoder pointer.
        unsafe { sys::opus_decoder_destroy(self.inner.as_ptr()) };
    }
}

// See the corresponding encoder safety note. Stateful methods require exclusive access.
unsafe impl Send for OpusDecoder {}

#[cfg(test)]
mod tests {
    use super::*;
    use std::f32::consts::TAU;

    fn speech_frame(frame: usize) -> [f32; FRAME_SIZE] {
        let mut pcm = [0.0; FRAME_SIZE];
        for (sample, value) in pcm.iter_mut().enumerate() {
            let t = (frame * FRAME_SIZE + sample) as f32 / SAMPLE_RATE as f32;
            let syllable = 0.55 + 0.45 * (TAU * 3.7 * t).sin().abs();
            *value = syllable
                * (0.22 * (TAU * 173.0 * t).sin()
                    + 0.10 * (TAU * 347.0 * t).sin()
                    + 0.05 * (TAU * 521.0 * t).sin());
        }
        pcm
    }

    #[test]
    fn bundled_libopus_is_1_6_1_and_dred_is_configured() {
        assert!(libopus_version().contains("1.6.1"), "{}", libopus_version());
        let encoder = OpusEncoder::new().expect("encoder");
        let decoder = OpusDecoder::new().expect("decoder");
        assert_eq!(encoder.dred_duration_10ms(), DEFAULT_DRED_DURATION_10MS);
        assert_eq!(encoder.dred_capability(), DredCapability::Available);
        assert_eq!(decoder.dred_capability(), DredCapability::Available);
    }

    #[test]
    fn primary_fec_and_plc_decode_one_frame() {
        let mut encoder = OpusEncoder::new().expect("encoder");
        let first = encoder.encode(&speech_frame(0)).expect("first").to_vec();
        let second = encoder.encode(&speech_frame(1)).expect("second").to_vec();

        let mut primary = OpusDecoder::new().expect("primary decoder");
        let mut output = [0.0; FRAME_SIZE];
        assert_eq!(
            primary.decode(&first, &mut output).expect("primary"),
            FRAME_SIZE
        );
        assert!(output.iter().all(|sample| sample.is_finite()));

        let mut recovery = OpusDecoder::new().expect("recovery decoder");
        assert_eq!(
            recovery.decode_fec(&second, &mut output).expect("FEC"),
            FRAME_SIZE
        );
        assert_eq!(recovery.decode_plc(&mut output).expect("PLC"), FRAME_SIZE);
    }

    #[test]
    fn decoder_reset_recreates_live_dred_resources() {
        let mut encoder = OpusEncoder::new().expect("encoder");
        let packets: Vec<Vec<u8>> = (0..100)
            .map(|frame| {
                encoder
                    .encode(&speech_frame(frame))
                    .expect("encode")
                    .to_vec()
            })
            .collect();
        let mut decoder = OpusDecoder::new().expect("decoder");
        let mut output = [0.0f32; FRAME_SIZE];
        assert_eq!(
            decoder.decode(&packets[0], &mut output).expect("primary"),
            FRAME_SIZE
        );
        assert!(matches!(
            decoder
                .parse_dred(&packets[99], 5 * FRAME_SIZE)
                .expect("parse before reset"),
            DredParseOutcome::Available(_)
        ));

        decoder.reset().expect("reset live decoder");
        assert_eq!(
            decoder
                .decode(&packets[0], &mut output)
                .expect("primary after reset"),
            FRAME_SIZE
        );
        assert!(matches!(
            decoder
                .parse_dred(&packets[99], 5 * FRAME_SIZE)
                .expect("parse after reset"),
            DredParseOutcome::Available(_)
        ));
    }

    #[test]
    fn validates_slice_contracts() {
        let mut encoder = OpusEncoder::new().expect("encoder");
        assert!(matches!(
            encoder.encode(&[0.0; FRAME_SIZE - 1]),
            Err(OpusError::InvalidPcmLength { .. })
        ));
        let mut decoder = OpusDecoder::new().expect("decoder");
        assert_eq!(
            decoder.decode(&[], &mut [0.0; FRAME_SIZE]),
            Err(OpusError::EmptyPacket)
        );
        assert!(matches!(
            decoder.decode_plc(&mut [0.0; FRAME_SIZE - 1]),
            Err(OpusError::OutputTooSmall { .. })
        ));
    }

    #[test]
    fn encoder_and_decoder_are_send() {
        fn assert_send<T: Send>() {}
        assert_send::<OpusEncoder>();
        assert_send::<OpusDecoder>();
    }

    #[test]
    fn dred_recovers_multiple_gap_frames_in_chronological_order() {
        const HISTORY_FRAMES: usize = 100;
        const LOST_FRAMES: usize = 5;

        let mut encoder = OpusEncoder::new().expect("encoder");
        let packets: Vec<Vec<u8>> = (0..HISTORY_FRAMES)
            .map(|frame| {
                encoder
                    .encode(&speech_frame(frame))
                    .expect("encode")
                    .to_vec()
            })
            .collect();

        // A real receiver has decoded everything before the four-frame gap.
        let recovery_index = HISTORY_FRAMES - 1;
        let gap_start = recovery_index - LOST_FRAMES;
        let mut decoder = OpusDecoder::new().expect("decoder");
        let mut output = [0.0; FRAME_SIZE];
        for packet in &packets[..gap_start] {
            decoder.decode(packet, &mut output).expect("history decode");
        }

        let info = match decoder
            .parse_dred(&packets[recovery_index], LOST_FRAMES * FRAME_SIZE)
            .expect("DRED parse")
        {
            DredParseOutcome::Available(info) => info,
            DredParseOutcome::NotPresent => panic!("Opus packet did not contain configured DRED"),
        };

        let mut recovered = 0usize;
        let mut total_energy = 0.0f32;
        // Explicit descending offsets are oldest-to-newest on the decoder timeline.
        // Offset one is intentionally reserved for the packet's dedicated classic FEC data.
        for lost in (2..=LOST_FRAMES).rev() {
            match decoder
                .decode_parsed_dred(lost * FRAME_SIZE, &mut output)
                .expect("DRED decode")
            {
                DredDecodeOutcome::Decoded { samples, .. } => {
                    assert_eq!(samples, FRAME_SIZE);
                    assert!(output.iter().all(|sample| sample.is_finite()));
                    total_energy += output.iter().map(|sample| sample * sample).sum::<f32>();
                    recovered += 1;
                }
                DredDecodeOutcome::OffsetNotCovered { .. } => {}
                DredDecodeOutcome::NotPresent => panic!("parsed DRED disappeared"),
            }
        }
        assert!(
            recovered >= 3,
            "expected multi-frame DRED coverage, got {recovered} frame(s): {info:?}"
        );
        assert!(total_energy > 1e-6, "DRED output was unexpectedly silent");
    }

    #[test]
    fn encoder_reset_removes_pre_authorization_dred_history() {
        let mut encoder = OpusEncoder::new().expect("encoder");
        for frame in 0..100 {
            encoder
                .encode(&speech_frame(frame))
                .expect("history encode");
        }

        // Without a reset, even a silent transition packet exposes energetic speech history.
        let silence = [0.0; FRAME_SIZE];
        let join_packet = encoder
            .encode(&silence)
            .expect("transition encode")
            .to_vec();
        let exposed_packet = encoder
            .encode(&silence)
            .expect("second transition encode")
            .to_vec();
        let mut exposed_decoder = OpusDecoder::new().expect("exposed decoder");
        let mut join_audio = [0.0; FRAME_SIZE];
        exposed_decoder
            .decode(&join_packet, &mut join_audio)
            .expect("join packet decode");
        let exposed_info = match exposed_decoder
            .parse_dred(&exposed_packet, 5 * FRAME_SIZE)
            .expect("exposed parse")
        {
            DredParseOutcome::Available(info) => info,
            DredParseOutcome::NotPresent => panic!("control packet did not expose DRED history"),
        };
        assert!(exposed_info.oldest_offset_samples >= 2 * FRAME_SIZE);
        let mut exposed_energy = 0.0f32;
        let mut exposed_frames = 0usize;
        // After decoding its first authorized packet, a new receiver can walk the hidden history
        // in chronological order and recover energetic pre-authorization speech.
        for offset_frames in (1..=4).rev() {
            let mut exposed_audio = [0.0; FRAME_SIZE];
            if let DredDecodeOutcome::Decoded {
                samples: FRAME_SIZE,
                ..
            } = exposed_decoder
                .decode_parsed_dred(offset_frames * FRAME_SIZE, &mut exposed_audio)
                .expect("exposed decode")
            {
                exposed_frames += 1;
                exposed_energy += exposed_audio
                    .iter()
                    .map(|sample| sample * sample)
                    .sum::<f32>();
            }
        }
        assert!(exposed_frames >= 2, "control lacked multi-frame history");
        assert!(
            exposed_energy > 1e-6,
            "control DRED history was silent: energy={exposed_energy:e}"
        );

        encoder.reset().expect("privacy reset");
        assert_eq!(
            encoder.dred_duration_10ms(),
            DEFAULT_DRED_DURATION_10MS,
            "privacy reset must not permanently disable DRED"
        );
        let protected_join_packet = encoder
            .encode(&silence)
            .expect("protected join encode")
            .to_vec();
        let protected_packet = encoder
            .encode(&silence)
            .expect("protected second encode")
            .to_vec();
        let mut protected_decoder = OpusDecoder::new().expect("protected decoder");
        let mut protected_audio = [0.0; FRAME_SIZE];
        protected_decoder
            .decode(&protected_join_packet, &mut protected_audio)
            .expect("protected join decode");
        protected_audio.fill(0.0);
        match protected_decoder
            .parse_dred(&protected_packet, 5 * FRAME_SIZE)
            .expect("protected parse")
        {
            DredParseOutcome::NotPresent => {}
            DredParseOutcome::Available(info) => {
                assert!(
                    info.oldest_offset_samples < FRAME_SIZE,
                    "reset packet retained a full pre-authorization frame: {info:?}"
                );
                assert!(matches!(
                    protected_decoder
                        .decode_parsed_dred(FRAME_SIZE, &mut protected_audio)
                        .expect("protected decode check"),
                    DredDecodeOutcome::OffsetNotCovered { .. }
                ));
            }
        }
        let protected_energy = protected_audio
            .iter()
            .map(|sample| sample * sample)
            .sum::<f32>();
        assert_eq!(
            protected_energy, 0.0,
            "reset exposed energetic pre-authorization DRED"
        );
    }

    #[test]
    fn healthy_packet_loss_policy_spends_no_dred_bits() {
        for packet_loss_percent in [5, 10] {
            let mut encoder = OpusEncoder::with_config(EncoderConfig {
                packet_loss_percent,
                ..EncoderConfig::default()
            })
            .expect("healthy-route encoder");
            let mut packet = Vec::new();
            for frame in 0..100 {
                packet = encoder
                    .encode(&speech_frame(frame))
                    .expect("healthy-route encode")
                    .to_vec();
            }
            let mut decoder = OpusDecoder::new().expect("DRED parser");
            assert_eq!(
                decoder
                    .parse_dred(&packet, 5 * FRAME_SIZE)
                    .expect("healthy-route DRED parse"),
                DredParseOutcome::NotPresent,
                "{packet_loss_percent}% policy unexpectedly spent bits on DRED"
            );
        }
    }
}
