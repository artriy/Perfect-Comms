use audiopus::coder::{Decoder, Encoder};
use audiopus::packet::Packet;
use audiopus::{Application, Bitrate, Channels, MutSignals, SampleRate, Signal};

pub const SAMPLE_RATE: i32 = 48_000;
pub const CHANNELS: usize = 1;
pub const FRAME_SIZE: usize = 960;
pub const BITRATE: i32 = 48_000;
pub const COMPLEXITY: u8 = 10;
pub const PACKET_LOSS_PERCENT: u8 = 15;
const MAX_PACKET: usize = 4000;

pub struct OpusCodec {
    encoder: Encoder,
    decoder: Decoder,
}

impl OpusCodec {
    pub fn new() -> Result<Self, audiopus::Error> {
        let mut encoder = Encoder::new(SampleRate::Hz48000, Channels::Mono, Application::Voip)?;
        encoder.set_bitrate(Bitrate::BitsPerSecond(BITRATE))?;
        encoder.set_complexity(COMPLEXITY)?;
        encoder.set_signal(Signal::Voice)?;
        encoder.set_vbr(true)?;
        encoder.set_vbr_constraint(true)?;
        encoder.set_dtx(false)?;
        encoder.set_inband_fec(true)?;
        encoder.set_packet_loss_perc(PACKET_LOSS_PERCENT)?;
        let decoder = Decoder::new(SampleRate::Hz48000, Channels::Mono)?;
        Ok(Self { encoder, decoder })
    }

    pub fn encode(&mut self, pcm: &[f32]) -> Vec<u8> {
        let mut out = vec![0u8; MAX_PACKET];
        match self.encoder.encode_float(pcm, &mut out) {
            Ok(n) => {
                out.truncate(n);
                out
            }
            Err(_) => Vec::new(),
        }
    }

    pub fn decode(&mut self, pkt: &[u8], out: &mut [f32]) -> usize {
        let packet = match Packet::try_from(pkt) {
            Ok(p) => p,
            Err(_) => return 0,
        };
        let signals = match MutSignals::try_from(out) {
            Ok(s) => s,
            Err(_) => return 0,
        };
        self.decoder
            .decode_float(Some(packet), signals, false)
            .unwrap_or(0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips_a_tone_frame() {
        let mut codec = OpusCodec::new().expect("opus codec init");
        let mut pcm = [0f32; FRAME_SIZE];
        for (i, s) in pcm.iter_mut().enumerate() {
            *s = (2.0 * std::f32::consts::PI * 440.0 * i as f32 / SAMPLE_RATE as f32).sin() * 0.5;
        }
        let packet = codec.encode(&pcm);
        assert!(!packet.is_empty(), "encoder produced an empty packet");

        let mut decoded = [0f32; FRAME_SIZE];
        let samples = codec.decode(&packet, &mut decoded);
        assert_eq!(samples, FRAME_SIZE, "decoded sample count != frame size");
    }
}
