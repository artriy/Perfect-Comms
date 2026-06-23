use crate::proto::{AudioFrame, FRAME_SAMPLES, SAMPLE_RATE};

pub const TONE_HZ: f32 = 440.0;

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
            let s0 = if i0 < 0 { self.last } else { mono_in[i0 as usize] };
            let i1 = i0 + 1;
            let s1 = if i1 < n as isize { mono_in[i1 as usize] } else { mono_in[n - 1] };
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

impl FrameAccumulator {
    pub fn new() -> FrameAccumulator {
        FrameAccumulator { buf: Vec::with_capacity(FRAME_SAMPLES * 2) }
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
            frames.push(AudioFrame { capture_ts_ns: ts_provider(), samples });
        }
        frames
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::proto::{FRAME_SAMPLES, SAMPLE_RATE};

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
}
