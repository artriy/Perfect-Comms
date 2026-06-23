pub const PROTO_VERSION: u32 = 1;
pub const SAMPLE_RATE: u32 = 48_000;
pub const CHANNELS: u16 = 1;
pub const FRAME_SAMPLES: usize = 960;
pub const FRAME_BYTES: usize = 8 + FRAME_SAMPLES * 4;

pub const TYPE_CONTROL: u8 = 0x01;
pub const TYPE_AUDIO: u8 = 0x02;

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
}
