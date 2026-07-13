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

// Cap on how many frames we conceal across a single RTP sequence gap. Beyond this a gap is treated
// as a stream restart (long silence / reconnect), not packet loss, so we don't flood the jitter
// buffer with concealment after a stall.
pub const MAX_CONCEAL_FRAMES: usize = 5;

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

    // Packet-loss concealment: synthesize a frame for a lost packet with no data available.
    pub fn decode_plc(&mut self, out: &mut [f32]) -> usize {
        let signals = match MutSignals::try_from(out) {
            Ok(s) => s,
            Err(_) => return 0,
        };
        self.decoder.decode_float(None, signals, true).unwrap_or(0)
    }

    // Recover the frame *before* `next_pkt` from that packet's in-band FEC (the encoder sets
    // inband_fec, so a packet carries redundant coding for the previous frame).
    pub fn decode_fec(&mut self, next_pkt: &[u8], out: &mut [f32]) -> usize {
        let packet = match Packet::try_from(next_pkt) {
            Ok(p) => p,
            Err(_) => return 0,
        };
        let signals = match MutSignals::try_from(out) {
            Ok(s) => s,
            Err(_) => return 0,
        };
        self.decoder
            .decode_float(Some(packet), signals, true)
            .unwrap_or(0)
    }
}

// Decode `data` (RTP payload with sequence number `seq`) against the peer's last in-order seq,
// emitting concealment frames for any gap: older losses via PLC, the frame immediately before
// `data` via this packet's in-band FEC, then the packet itself. Late and duplicate RTP is dropped:
// its playout slot has already been concealed, and decoding it would both repeat old speech and
// corrupt the stateful Opus decoder's forward timeline.
pub fn decode_with_concealment(
    codec: &mut OpusCodec,
    last: Option<u16>,
    seq: u16,
    data: &[u8],
) -> (Vec<Vec<f32>>, bool) {
    let mut frames: Vec<Vec<f32>> = Vec::new();
    let mut pcm = [0f32; FRAME_SIZE];
    let last = match last {
        None => {
            let n = codec.decode(data, &mut pcm);
            if n > 0 {
                frames.push(pcm[..n].to_vec());
            }
            return (frames, true);
        }
        Some(l) => l,
    };
    let delta = seq.wrapping_sub(last) as i16;
    if delta <= 0 {
        return (frames, false);
    }
    let lost = (delta - 1) as usize;
    if lost > 0 && lost <= MAX_CONCEAL_FRAMES {
        for _ in 0..lost - 1 {
            let mut plc = [0f32; FRAME_SIZE];
            let n = codec.decode_plc(&mut plc);
            if n > 0 {
                frames.push(plc[..n].to_vec());
            }
        }
        let mut fec = [0f32; FRAME_SIZE];
        let n = codec.decode_fec(data, &mut fec);
        if n > 0 {
            frames.push(fec[..n].to_vec());
        }
    }
    let n = codec.decode(data, &mut pcm);
    if n > 0 {
        frames.push(pcm[..n].to_vec());
    }
    (frames, true)
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

    #[test]
    fn plc_and_fec_yield_full_frames() {
        let mut codec = OpusCodec::new().expect("opus codec init");
        let mut pcm = [0f32; FRAME_SIZE];
        for (i, s) in pcm.iter_mut().enumerate() {
            *s = (2.0 * std::f32::consts::PI * 440.0 * i as f32 / SAMPLE_RATE as f32).sin() * 0.5;
        }
        let next = codec.encode(&pcm);
        assert!(!next.is_empty());

        let mut plc = [0f32; FRAME_SIZE];
        assert_eq!(
            codec.decode_plc(&mut plc),
            FRAME_SIZE,
            "PLC must produce a full concealment frame"
        );

        let mut fec = [0f32; FRAME_SIZE];
        assert_eq!(
            codec.decode_fec(&next, &mut fec),
            FRAME_SIZE,
            "FEC recovery must produce a full frame"
        );
    }

    #[test]
    fn concealment_emits_extra_frames_only_for_real_gaps() {
        let mut codec = OpusCodec::new().expect("opus codec init");
        let mut pcm = [0f32; FRAME_SIZE];
        for (i, s) in pcm.iter_mut().enumerate() {
            *s = (2.0 * std::f32::consts::PI * 440.0 * i as f32 / SAMPLE_RATE as f32).sin() * 0.5;
        }
        let pkt = codec.encode(&pcm);
        assert!(!pkt.is_empty());

        // First packet for a peer: one frame, advance.
        let (f, adv) = decode_with_concealment(&mut codec, None, 100, &pkt);
        assert_eq!(f.len(), 1);
        assert!(adv);

        // In-order next packet (seq 101): one frame, advance.
        let (f, adv) = decode_with_concealment(&mut codec, Some(100), 101, &pkt);
        assert_eq!(f.len(), 1);
        assert!(adv);

        // Gap of 2 lost packets (last=101, seq=104): 1 PLC + 1 FEC + the live frame = 3.
        let (f, adv) = decode_with_concealment(&mut codec, Some(101), 104, &pkt);
        assert_eq!(f.len(), 3, "two-packet gap must conceal then play");
        assert!(adv);

        // Out-of-order / duplicate (seq <= last): its slot was already concealed, so drop it.
        let (f, adv) = decode_with_concealment(&mut codec, Some(104), 102, &pkt);
        assert!(f.is_empty());
        assert!(!adv, "late packet must not rewind gap tracking");

        // Huge gap beyond the cap: treat as restart, no concealment flood (just the live frame).
        let (f, adv) = decode_with_concealment(&mut codec, Some(104), 1000, &pkt);
        assert_eq!(
            f.len(),
            1,
            "oversized gap is a restart, not a conceal flood"
        );
        assert!(adv);
    }
}
