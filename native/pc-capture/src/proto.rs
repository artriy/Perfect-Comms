pub const PROTO_VERSION: u32 = 1;
pub const SAMPLE_RATE: u32 = 48_000;
pub const CHANNELS: u16 = 1;
pub const FRAME_SAMPLES: usize = 960;
pub const FRAME_BYTES: usize = 8 + FRAME_SAMPLES * 4;

pub const TYPE_CONTROL: u8 = 0x01;
pub const TYPE_AUDIO: u8 = 0x02;

use std::io::Read;

#[derive(Debug, Clone)]
pub struct AudioFrame {
    pub capture_ts_ns: u64,
    pub samples: Vec<f32>,
}

#[derive(Debug)]
pub enum Frame {
    Control(String),
    Audio(AudioFrame),
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
            Ok(Frame::Audio(AudioFrame { capture_ts_ns: ts, samples }))
        }
        other => Err(DecodeError::BadType(other)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frozen_constants_match_contract() {
        assert_eq!(PROTO_VERSION, 1);
        assert_eq!(SAMPLE_RATE, 48_000);
        assert_eq!(CHANNELS, 1);
        assert_eq!(FRAME_SAMPLES, 960);
        assert_eq!(FRAME_BYTES, 8 + 960 * 4);
        assert_eq!(TYPE_CONTROL, 0x01);
        assert_eq!(TYPE_AUDIO, 0x02);
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
        let frame = AudioFrame { capture_ts_ns: 0x0102_0304_0506_0708, samples };
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
}
