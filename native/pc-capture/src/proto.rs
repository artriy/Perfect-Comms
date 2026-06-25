pub const PROTO_VERSION: u32 = 2;
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
    pub capture_ts_ns: u64,
    pub samples: Vec<f32>,
}

#[derive(Debug, Clone)]
pub struct AudioOutFrame {
    pub samples: Vec<f32>,
}

#[derive(Debug)]
pub enum Frame {
    Control(String),
    // Decoded inbound audio is never read by the helper (audio flows helper->mod only); kept for the wire contract and tests.
    #[allow(dead_code)]
    Audio(AudioFrame),
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
                capture_ts_ns: ts,
                samples,
            }))
        }
        other => Err(DecodeError::BadType(other)),
    }
}

use std::collections::VecDeque;

pub const RING_CAPACITY: usize = 8;

pub struct AudioRing {
    capacity: usize,
    queue: VecDeque<AudioFrame>,
    dropped: u64,
}

impl AudioRing {
    pub fn new(capacity: usize) -> AudioRing {
        AudioRing {
            capacity: capacity.max(1),
            queue: VecDeque::with_capacity(capacity.max(1)),
            dropped: 0,
        }
    }

    pub fn push(&mut self, frame: AudioFrame) {
        if self.queue.len() == self.capacity {
            self.queue.pop_front();
            self.dropped += 1;
        }
        self.queue.push_back(frame);
    }

    pub fn pop(&mut self) -> Option<AudioFrame> {
        self.queue.pop_front()
    }

    #[cfg(test)]
    pub fn len(&self) -> usize {
        self.queue.len()
    }

    pub fn dropped(&self) -> u64 {
        self.dropped
    }
}

pub struct PlaybackRing {
    capacity_pairs: usize,
    queue: VecDeque<(f32, f32)>,
    dropped: u64,
}

impl PlaybackRing {
    pub fn new(capacity_pairs: usize) -> PlaybackRing {
        let cap = capacity_pairs.max(2);
        PlaybackRing {
            capacity_pairs: cap,
            queue: VecDeque::with_capacity(cap),
            dropped: 0,
        }
    }

    pub fn push(&mut self, interleaved: &[f32]) {
        let pairs = interleaved.len() / 2;
        for i in 0..pairs {
            if self.queue.len() >= self.capacity_pairs {
                self.queue.pop_front();
                self.dropped += 1;
            }
            self.queue
                .push_back((interleaved[2 * i], interleaved[2 * i + 1]));
        }
    }

    pub fn pop_stereo(&mut self) -> Option<(f32, f32)> {
        self.queue.pop_front()
    }

    pub fn len(&self) -> usize {
        self.queue.len()
    }

    #[cfg(test)]
    pub fn dropped(&self) -> u64 {
        self.dropped
    }
}

use serde::{Deserialize, Serialize};

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
        hpf: bool,
    },
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

pub fn level_json(peak: f32) -> String {
    serde_json::to_string(&LevelMsg { op: "level", peak }).expect("level serialize")
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frozen_constants_match_contract() {
        assert_eq!(PROTO_VERSION, 2);
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
        let op = parse_inbound(r#"{"op":"set-dsp","aec":true,"agc":false,"ns":true,"hpf":false}"#)
            .unwrap();
        match op {
            InboundOp::SetDsp { aec, agc, ns, hpf } => {
                assert!(aec);
                assert!(!agc);
                assert!(ns);
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
            capture_ts_ns: 0x0102_0304_0506_0708,
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

    fn frame_with(ts: u64) -> AudioFrame {
        AudioFrame {
            capture_ts_ns: ts,
            samples: vec![0.0; FRAME_SAMPLES],
        }
    }

    #[test]
    fn ring_is_fifo_when_not_full() {
        let mut ring = AudioRing::new(4);
        ring.push(frame_with(1));
        ring.push(frame_with(2));
        ring.push(frame_with(3));
        assert_eq!(ring.len(), 3);
        assert_eq!(ring.dropped(), 0);
        assert_eq!(ring.pop().unwrap().capture_ts_ns, 1);
        assert_eq!(ring.pop().unwrap().capture_ts_ns, 2);
        assert_eq!(ring.pop().unwrap().capture_ts_ns, 3);
        assert!(ring.pop().is_none());
    }

    #[test]
    fn ring_drops_oldest_and_counts_when_full() {
        let mut ring = AudioRing::new(3);
        ring.push(frame_with(1));
        ring.push(frame_with(2));
        ring.push(frame_with(3));
        ring.push(frame_with(4));
        ring.push(frame_with(5));
        assert_eq!(ring.len(), 3);
        assert_eq!(ring.dropped(), 2);
        assert_eq!(ring.pop().unwrap().capture_ts_ns, 3);
        assert_eq!(ring.pop().unwrap().capture_ts_ns, 4);
        assert_eq!(ring.pop().unwrap().capture_ts_ns, 5);
        assert!(ring.pop().is_none());
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
        assert_eq!(v["proto"], 2);
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

        let lv: serde_json::Value = serde_json::from_str(&level_json(0.5)).unwrap();
        assert_eq!(lv["op"], "level");
        assert!((lv["peak"].as_f64().unwrap() - 0.5).abs() < 1e-6);

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
