use std::collections::{HashMap, VecDeque};

use crate::gamestate::GameState;

pub const GAIN_GLIDE_K: f32 = 0.002;
pub const PLAYBACK_MIX_PEAK_CEILING: f32 = 0.92;
pub const PLAYBACK_MIX_LIMITER_RELEASE_PER_FRAME: f32 = 0.05;
const PAN_FAR_SIDE: f32 = 0.25;

const RADIO_DRIVE: f32 = 2.0;
const RADIO_LEVEL: f32 = 0.75;
const WALL_DRY: f32 = 0.85;
const GHOST_DRY: f32 = 0.6;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FilterMode {
    None,
    Ghost,
    Radio,
    WallMuffle,
    ListenerMuffle,
}

impl FilterMode {
    pub fn from_i32(v: i32) -> FilterMode {
        match v {
            1 => FilterMode::Ghost,
            2 => FilterMode::Radio,
            3 => FilterMode::WallMuffle,
            4 => FilterMode::ListenerMuffle,
            _ => FilterMode::None,
        }
    }
}

pub fn pan_gains(pan: f32) -> (f32, f32) {
    let pan = pan.clamp(-1.0, 1.0);
    let far_gain =
        PAN_FAR_SIDE + (1.0 - PAN_FAR_SIDE) * (pan.abs() * (std::f32::consts::PI / 2.0)).cos();
    let left = if pan > 0.0 { far_gain } else { 1.0 };
    let right = if pan < 0.0 { far_gain } else { 1.0 };
    (left, right)
}

fn measure_peak(samples: &[f32]) -> f32 {
    let mut peak = 0.0f32;
    for &s in samples {
        let abs = s.abs();
        if abs > peak {
            peak = abs;
        }
    }
    peak
}

fn playback_mix_limiter_gain(peak: f32) -> f32 {
    if peak <= 0.0 || peak <= PLAYBACK_MIX_PEAK_CEILING {
        1.0
    } else {
        PLAYBACK_MIX_PEAK_CEILING / peak
    }
}

#[derive(Clone, Copy)]
struct Biquad {
    b0: f32,
    b1: f32,
    b2: f32,
    a1: f32,
    a2: f32,
}

impl Biquad {
    fn coeffs(f0: f32, q: f32) -> (f32, f32) {
        let w0 = 2.0 * std::f32::consts::PI * f0 / crate::codec::SAMPLE_RATE as f32;
        (w0.cos(), w0.sin() / (2.0 * q))
    }

    fn lowpass(f0: f32, q: f32) -> Biquad {
        let (cw, alpha) = Biquad::coeffs(f0, q);
        let a0 = 1.0 + alpha;
        Biquad {
            b0: (1.0 - cw) / 2.0 / a0,
            b1: (1.0 - cw) / a0,
            b2: (1.0 - cw) / 2.0 / a0,
            a1: -2.0 * cw / a0,
            a2: (1.0 - alpha) / a0,
        }
    }

    fn highpass(f0: f32, q: f32) -> Biquad {
        let (cw, alpha) = Biquad::coeffs(f0, q);
        let a0 = 1.0 + alpha;
        Biquad {
            b0: (1.0 + cw) / 2.0 / a0,
            b1: -(1.0 + cw) / a0,
            b2: (1.0 + cw) / 2.0 / a0,
            a1: -2.0 * cw / a0,
            a2: (1.0 - alpha) / a0,
        }
    }

    fn process(&self, z1: &mut f32, z2: &mut f32, x: f32) -> f32 {
        let y = self.b0 * x + *z1;
        *z1 = self.b1 * x - self.a1 * y + *z2;
        *z2 = self.b2 * x - self.a2 * y;
        y
    }
}

fn apply_filter(
    mode: FilterMode,
    lp650: &Biquad,
    hp650: &Biquad,
    z1: &mut f32,
    z2: &mut f32,
    s: f32,
) -> f32 {
    match mode {
        FilterMode::Radio => {
            let h = hp650.process(z1, z2, s);
            (h * RADIO_DRIVE).tanh() * RADIO_LEVEL
        }
        FilterMode::WallMuffle | FilterMode::ListenerMuffle => lp650.process(z1, z2, s) * WALL_DRY,
        FilterMode::Ghost => s * GHOST_DRY,
        FilterMode::None => s,
    }
}

struct Glide {
    left: f32,
    right: f32,
    mode: FilterMode,
    bz1: f32,
    bz2: f32,
}

pub struct Mixer {
    limiter_gain: f32,
    glide: HashMap<String, Glide>,
    lp650: Biquad,
    hp650: Biquad,
}

impl Default for Mixer {
    fn default() -> Self {
        Self::new()
    }
}

impl Mixer {
    pub fn new() -> Mixer {
        Mixer {
            limiter_gain: 1.0,
            glide: HashMap::new(),
            lp650: Biquad::lowpass(650.0, 0.7),
            hp650: Biquad::highpass(650.0, 0.9),
        }
    }

    pub fn mix(&mut self, per_peer: &[(String, &[f32])], gs: &GameState, out_stereo: &mut [f32]) {
        for s in out_stereo.iter_mut() {
            *s = 0.0;
        }
        let snap = gs.snapshot();
        if snap.local.deafened {
            return;
        }
        let frames = out_stereo.len() / 2;
        let (lp650, hp650) = (self.lp650, self.hp650);
        for (peer_id, mono) in per_peer {
            let (target, mode) = match snap.peers.get(peer_id) {
                Some(p) => {
                    let (lg, rg) = pan_gains(p.pan);
                    ((lg * p.gain, rg * p.gain), FilterMode::from_i32(p.mode))
                }
                None => ((0.0, 0.0), FilterMode::None),
            };
            let g = self.glide.entry(peer_id.clone()).or_insert(Glide {
                left: target.0,
                right: target.1,
                mode,
                bz1: 0.0,
                bz2: 0.0,
            });
            if g.mode != mode {
                g.mode = mode;
                g.bz1 = 0.0;
                g.bz2 = 0.0;
            }
            let n = frames.min(mono.len());
            for f in 0..n {
                let s = apply_filter(mode, &lp650, &hp650, &mut g.bz1, &mut g.bz2, mono[f]);
                g.left += GAIN_GLIDE_K * (target.0 - g.left);
                g.right += GAIN_GLIDE_K * (target.1 - g.right);
                out_stereo[2 * f] += s * g.left;
                out_stereo[2 * f + 1] += s * g.right;
            }
        }

        let master = snap.master.clamp(0.0, 2.0);
        if master != 1.0 {
            for s in out_stereo.iter_mut() {
                *s *= master;
            }
        }

        let peak = measure_peak(out_stereo);
        let target_gain = playback_mix_limiter_gain(peak);
        if target_gain < self.limiter_gain {
            self.limiter_gain = target_gain;
        } else {
            self.limiter_gain =
                1.0_f32.min(self.limiter_gain + PLAYBACK_MIX_LIMITER_RELEASE_PER_FRAME);
        }
        if self.limiter_gain < 1.0 {
            for s in out_stereo.iter_mut() {
                *s *= self.limiter_gain;
            }
        }
        for s in out_stereo.iter_mut() {
            *s = s.clamp(-1.0, 1.0);
        }

        self.glide
            .retain(|k, _| snap.peers.contains_key(k) || per_peer.iter().any(|(pid, _)| pid == k));
    }
}

pub const JITTER_PRIME_FRAMES: usize = 2;
pub const JITTER_CAP_FRAMES: usize = 12;

struct PeerQueue {
    frames: VecDeque<Vec<f32>>,
    primed: bool,
}

pub struct PeerJitter {
    peers: HashMap<String, PeerQueue>,
    prime: usize,
    cap: usize,
}

impl Default for PeerJitter {
    fn default() -> Self {
        PeerJitter::new()
    }
}

impl PeerJitter {
    pub fn new() -> PeerJitter {
        PeerJitter::with_limits(JITTER_PRIME_FRAMES, JITTER_CAP_FRAMES)
    }

    pub fn with_limits(prime: usize, cap: usize) -> PeerJitter {
        let prime = prime.max(1);
        PeerJitter {
            peers: HashMap::new(),
            prime,
            cap: cap.max(prime),
        }
    }

    pub fn push(&mut self, peer: &str, frame: Vec<f32>) {
        let q = self
            .peers
            .entry(peer.to_string())
            .or_insert_with(|| PeerQueue {
                frames: VecDeque::new(),
                primed: false,
            });
        if q.frames.len() >= self.cap {
            q.frames.pop_front();
        }
        q.frames.push_back(frame);
    }

    pub fn remove(&mut self, peer: &str) {
        self.peers.remove(peer);
    }

    pub fn playout_round(&mut self) -> Vec<(String, Vec<f32>)> {
        let prime = self.prime;
        let mut out = Vec::new();
        for (peer, q) in self.peers.iter_mut() {
            if !q.primed {
                if q.frames.len() >= prime {
                    q.primed = true;
                } else {
                    continue;
                }
            }
            match q.frames.pop_front() {
                Some(frame) => out.push((peer.clone(), frame)),
                None => q.primed = false,
            }
        }
        out
    }

    pub fn is_idle(&self) -> bool {
        self.peers.values().all(|q| q.frames.is_empty())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::gamestate::{GameState, LocalState, PeerState};

    fn approx(a: f32, b: f32) {
        assert!((a - b).abs() < 1e-5, "expected {b}, got {a}");
    }

    fn approx_tol(a: f32, b: f32, tol: f32) {
        assert!((a - b).abs() < tol, "expected {b}, got {a}");
    }

    #[test]
    fn jitter_spreads_a_burst_instead_of_dropping_it() {
        let mut jb = PeerJitter::with_limits(2, 12);
        for i in 0..4u8 {
            jb.push("a", vec![i as f32]);
        }
        let mut got = Vec::new();
        for _ in 0..4 {
            let round = jb.playout_round();
            assert_eq!(round.len(), 1, "exactly one frame per peer per round");
            got.push(round[0].1[0]);
        }
        assert_eq!(
            got,
            vec![0.0, 1.0, 2.0, 3.0],
            "frames played in arrival order"
        );
        assert!(jb.is_idle());
        assert!(jb.playout_round().is_empty());
    }

    #[test]
    fn jitter_drops_oldest_on_overflow_not_newest() {
        let mut jb = PeerJitter::with_limits(1, 3);
        for i in 0..5u8 {
            jb.push("a", vec![i as f32]);
        }
        let got: Vec<f32> = (0..3).map(|_| jb.playout_round()[0].1[0]).collect();
        assert_eq!(got, vec![2.0, 3.0, 4.0], "cap keeps the 3 newest frames");
    }

    #[test]
    fn jitter_waits_for_prime_then_plays() {
        let mut jb = PeerJitter::with_limits(2, 12);
        jb.push("a", vec![1.0]);
        assert!(jb.playout_round().is_empty(), "below prime: no playout yet");
        jb.push("a", vec![2.0]);
        assert_eq!(jb.playout_round().len(), 1, "reached prime: playout starts");
    }

    #[test]
    fn lowpass_passes_dc_highpass_blocks_it() {
        let lp = Biquad::lowpass(650.0, 0.7);
        let hp = Biquad::highpass(650.0, 0.9);
        let (mut lz1, mut lz2, mut hz1, mut hz2) = (0.0f32, 0.0f32, 0.0f32, 0.0f32);
        let mut lp_out = 0.0;
        let mut hp_out = 0.0;
        for _ in 0..4000 {
            lp_out = lp.process(&mut lz1, &mut lz2, 1.0);
            hp_out = hp.process(&mut hz1, &mut hz2, 1.0);
        }
        approx_tol(lp_out, 1.0, 1e-2);
        approx_tol(hp_out, 0.0, 1e-2);
    }

    #[test]
    fn radio_filter_is_bounded_and_audible() {
        let lp = Biquad::lowpass(650.0, 0.7);
        let hp = Biquad::highpass(650.0, 0.9);
        let (mut z1, mut z2) = (0.0f32, 0.0f32);
        let mut peak = 0.0f32;
        for i in 0..960 {
            let x = (2.0 * std::f32::consts::PI * 2000.0 * i as f32 / 48000.0).sin();
            let y = apply_filter(FilterMode::Radio, &lp, &hp, &mut z1, &mut z2, x);
            peak = peak.max(y.abs());
        }
        assert!(peak > 0.0, "radio passband produced silence");
        assert!(
            peak <= RADIO_LEVEL + 1e-3,
            "radio output exceeds level trim: {peak}"
        );
    }

    #[test]
    fn filter_dry_levels_match_csharp() {
        let lp = Biquad::lowpass(650.0, 0.7);
        let hp = Biquad::highpass(650.0, 0.9);

        let (mut z1, mut z2) = (0.0f32, 0.0f32);
        approx(
            apply_filter(FilterMode::None, &lp, &hp, &mut z1, &mut z2, 0.42),
            0.42,
        );

        let (mut gz1, mut gz2) = (0.0f32, 0.0f32);
        approx(
            apply_filter(FilterMode::Ghost, &lp, &hp, &mut gz1, &mut gz2, 0.5),
            0.5 * GHOST_DRY,
        );

        let (mut wz1, mut wz2) = (0.0f32, 0.0f32);
        let mut wall = 0.0;
        for _ in 0..4000 {
            wall = apply_filter(FilterMode::WallMuffle, &lp, &hp, &mut wz1, &mut wz2, 1.0);
        }
        approx_tol(wall, WALL_DRY, 1e-2);
    }

    #[test]
    fn pan_gains_match_csharp() {
        let (l, r) = pan_gains(0.0);
        approx(l, 1.0);
        approx(r, 1.0);
        let (l, r) = pan_gains(1.0);
        approx(l, 0.25);
        approx(r, 1.0);
        let (l, r) = pan_gains(-1.0);
        approx(l, 1.0);
        approx(r, 0.25);
    }

    fn gs_with(local: LocalState, master: f32, peers: Vec<(&str, PeerState)>) -> GameState {
        let gs = GameState::new();
        gs.set_local(local);
        gs.set_master(master);
        for (id, p) in peers {
            gs.upsert_peer(id.to_string(), p);
        }
        gs
    }

    #[test]
    fn mix_applies_resolved_gain_and_pan_end_to_end() {
        let gs = gs_with(
            LocalState { deafened: false },
            1.0,
            vec![(
                "p",
                PeerState {
                    gain: 0.5,
                    pan: 1.0,
                    mode: 0,
                },
            )],
        );
        let mut mixer = Mixer::new();
        let mono = vec![0.1f32; 960];
        let per_peer = vec![("p".to_string(), mono.as_slice())];
        let mut out = vec![0.0f32; 1920];
        mixer.mix(&per_peer, &gs, &mut out);
        approx(out[0], 0.1 * PAN_FAR_SIDE * 0.5);
        approx(out[1], 0.1 * 0.5);
    }

    #[test]
    fn deafened_produces_silence() {
        let gs = gs_with(
            LocalState { deafened: true },
            1.0,
            vec![(
                "p",
                PeerState {
                    gain: 1.0,
                    pan: 0.0,
                    mode: 0,
                },
            )],
        );
        let mut mixer = Mixer::new();
        let mono = vec![0.5f32; 960];
        let per_peer = vec![("p".to_string(), mono.as_slice())];
        let mut out = vec![1.0f32; 1920];
        mixer.mix(&per_peer, &gs, &mut out);
        assert!(out.iter().all(|&s| s == 0.0));
    }

    #[test]
    fn master_volume_scales_output() {
        let gs = gs_with(
            LocalState { deafened: false },
            0.5,
            vec![(
                "p",
                PeerState {
                    gain: 0.5,
                    pan: 1.0,
                    mode: 0,
                },
            )],
        );
        let mut mixer = Mixer::new();
        let mono = vec![0.1f32; 960];
        let per_peer = vec![("p".to_string(), mono.as_slice())];
        let mut out = vec![0.0f32; 1920];
        mixer.mix(&per_peer, &gs, &mut out);
        approx(out[0], 0.1 * PAN_FAR_SIDE * 0.5 * 0.5);
        approx(out[1], 0.1 * 0.5 * 0.5);
    }

    #[test]
    fn limiter_and_clamp_keep_output_in_range() {
        let gs = gs_with(
            LocalState { deafened: false },
            1.0,
            vec![(
                "p",
                PeerState {
                    gain: 1.0,
                    pan: 0.0,
                    mode: 0,
                },
            )],
        );
        let mut mixer = Mixer::new();
        let mono = vec![1.0f32; 960];
        let per_peer = vec![("p".to_string(), mono.as_slice())];
        let mut out = vec![0.0f32; 1920];
        mixer.mix(&per_peer, &gs, &mut out);
        assert!(out.iter().all(|&s| (-1.0..=1.0).contains(&s)));
    }
}
