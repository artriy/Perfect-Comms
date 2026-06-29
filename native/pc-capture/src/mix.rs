use std::collections::{HashMap, VecDeque};

use crate::gamestate::GameState;

pub const LOW_VOLUME_FLOOR: f32 = 0.06;
pub const GAIN_GLIDE_K: f32 = 0.002;
pub const PLAYBACK_MIX_PEAK_CEILING: f32 = 0.92;
pub const PLAYBACK_MIX_LIMITER_RELEASE_PER_FRAME: f32 = 0.05;
const PAN_FAR_SIDE: f32 = 0.25;

pub const ROLE_FORCE_AUDIBLE: u32 = 1;

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

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FalloffMode {
    Linear,
    Smooth,
    VoiceFocused,
}

impl FalloffMode {
    pub fn from_i32(v: i32) -> FalloffMode {
        match v {
            1 => FalloffMode::Smooth,
            2 => FalloffMode::VoiceFocused,
            _ => FalloffMode::Linear,
        }
    }
}

pub fn apply_falloff(distance: f32, max_distance: f32, mode: FalloffMode) -> f32 {
    if max_distance <= 0.0 {
        return 0.0;
    }
    let t = (distance / max_distance).clamp(0.0, 1.0);
    match mode {
        FalloffMode::Smooth => 1.0 - smooth_step(t),
        FalloffMode::VoiceFocused => {
            if t < 0.35 {
                1.0
            } else {
                ((1.0 - t) / 0.65).powf(1.35)
            }
        }
        FalloffMode::Linear => 1.0 - t,
    }
}

fn smooth_step(t: f32) -> f32 {
    t * t * (3.0 - 2.0 * t)
}

pub fn get_pan(mic_x: f32, target_x: f32) -> f32 {
    ((target_x - mic_x) / 3.0).clamp(-1.0, 1.0)
}

pub fn pan_gains(pan: f32) -> (f32, f32) {
    let pan = pan.clamp(-1.0, 1.0);
    let far_gain =
        PAN_FAR_SIDE + (1.0 - PAN_FAR_SIDE) * (pan.abs() * (std::f32::consts::PI / 2.0)).cos();
    let left = if pan > 0.0 { far_gain } else { 1.0 };
    let right = if pan < 0.0 { far_gain } else { 1.0 };
    (left, right)
}

#[allow(clippy::too_many_arguments)]
pub fn peer_target_gains(
    local_x: f32,
    local_y: f32,
    peer_x: f32,
    peer_y: f32,
    peer_volume: f32,
    muted: bool,
    max_distance: f32,
    falloff: FalloffMode,
    role_flags: u32,
    nvol: f32,
) -> (f32, f32) {
    if muted {
        return (0.0, 0.0);
    }

    let vol = if role_flags & ROLE_FORCE_AUDIBLE != 0 {
        peer_volume
    } else {
        let dx = peer_x - local_x;
        let dy = peer_y - local_y;
        let dist = (dx * dx + dy * dy).sqrt();
        let mut v = apply_falloff(dist, max_distance, falloff);
        if v < LOW_VOLUME_FLOOR {
            v = 0.0;
        }

        v * peer_volume * nvol
    };
    let pan = get_pan(local_x, peer_x);
    let (lg, rg) = pan_gains(pan);
    (lg * vol, rg * vol)
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
    master: f32,
    deafened: bool,
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
            master: 1.0,
            deafened: false,
            limiter_gain: 1.0,
            glide: HashMap::new(),

            lp650: Biquad::lowpass(650.0, 0.7),
            hp650: Biquad::highpass(650.0, 0.9),
        }
    }

    pub fn set_master(&mut self, volume: f32) {
        self.master = volume.clamp(0.0, 2.0);
    }

    pub fn set_deafened(&mut self, deafened: bool) {
        self.deafened = deafened;
    }

    pub fn mix(&mut self, per_peer: &[(String, &[f32])], gs: &GameState, out_stereo: &mut [f32]) {
        for s in out_stereo.iter_mut() {
            *s = 0.0;
        }
        if self.deafened {
            return;
        }
        let snap = gs.snapshot();
        let frames = out_stereo.len() / 2;

        let (lp650, hp650) = (self.lp650, self.hp650);
        for (peer_id, mono) in per_peer {
            let (target, mode) = match snap.peers.get(peer_id) {
                Some(p) => (
                    peer_target_gains(
                        snap.local.x,
                        snap.local.y,
                        p.x,
                        p.y,
                        p.volume,
                        p.muted,
                        snap.max_distance,
                        snap.falloff,
                        p.role_flags,
                        p.nvol,
                    ),
                    FilterMode::from_i32(p.mode),
                ),
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

        if self.master != 1.0 {
            for s in out_stereo.iter_mut() {
                *s *= self.master;
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

    fn approx(a: f32, b: f32) {
        assert!((a - b).abs() < 1e-5, "expected {b}, got {a}");
    }

    fn approx_tol(a: f32, b: f32, tol: f32) {
        assert!((a - b).abs() < tol, "expected {b}, got {a}");
    }

    #[test]
    fn falloff_linear_matches_csharp() {
        approx(apply_falloff(0.0, 6.0, FalloffMode::Linear), 1.0);
        approx(apply_falloff(3.0, 6.0, FalloffMode::Linear), 0.5);
        approx(apply_falloff(6.0, 6.0, FalloffMode::Linear), 0.0);
        approx(apply_falloff(12.0, 6.0, FalloffMode::Linear), 0.0);
        approx(apply_falloff(1.0, 0.0, FalloffMode::Linear), 0.0);
    }

    #[test]
    fn falloff_smooth_matches_csharp() {
        approx(apply_falloff(1.5, 6.0, FalloffMode::Smooth), 0.84375);
        approx(apply_falloff(3.0, 6.0, FalloffMode::Smooth), 0.5);
    }

    #[test]
    fn falloff_voicefocused_matches_csharp() {
        approx(apply_falloff(1.2, 6.0, FalloffMode::VoiceFocused), 1.0);
        approx_tol(
            apply_falloff(3.0, 6.0, FalloffMode::VoiceFocused),
            0.701741,
            1e-3,
        );
    }

    #[test]
    fn pan_matches_csharp() {
        approx(get_pan(0.0, 3.0), 1.0);
        approx(get_pan(0.0, -3.0), -1.0);
        approx(get_pan(0.0, 1.5), 0.5);
        approx(get_pan(0.0, 30.0), 1.0);
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
        let (l, r) = pan_gains(0.5);
        let far = 0.25 + 0.75 * (0.5 * std::f32::consts::PI / 2.0).cos();
        approx(l, far);
        approx(r, 1.0);
    }

    #[test]
    fn peer_target_gains_match_csharp_formula() {
        let (l, r) = peer_target_gains(
            0.0,
            0.0,
            3.0,
            0.0,
            1.0,
            false,
            6.0,
            FalloffMode::Linear,
            0,
            1.0,
        );
        approx(l, 0.125);
        approx(r, 0.5);

        let (l, r) = peer_target_gains(
            0.0,
            0.0,
            3.0,
            0.0,
            2.0,
            false,
            6.0,
            FalloffMode::Linear,
            0,
            1.0,
        );
        approx(l, 0.25);
        approx(r, 1.0);

        let (l, r) = peer_target_gains(
            0.0,
            0.0,
            3.0,
            0.0,
            1.0,
            true,
            6.0,
            FalloffMode::Linear,
            0,
            1.0,
        );
        approx(l, 0.0);
        approx(r, 0.0);

        let (l, r) = peer_target_gains(
            0.0,
            0.0,
            5.9,
            0.0,
            1.0,
            false,
            6.0,
            FalloffMode::Linear,
            0,
            1.0,
        );
        approx(l, 0.0);
        approx(r, 0.0);
    }

    #[test]
    fn force_audible_bypasses_distance_falloff() {
        let (l, r) = peer_target_gains(
            0.0,
            0.0,
            50.0,
            0.0,
            1.0,
            false,
            6.0,
            FalloffMode::Linear,
            ROLE_FORCE_AUDIBLE,
            1.0,
        );

        approx(r, 1.0);
        approx(l, PAN_FAR_SIDE);

        let (l, r) = peer_target_gains(
            0.0,
            0.0,
            1.0,
            0.0,
            1.0,
            true,
            6.0,
            FalloffMode::Linear,
            ROLE_FORCE_AUDIBLE,
            1.0,
        );
        approx(l, 0.0);
        approx(r, 0.0);
    }

    #[test]
    fn nvol_attenuates_proximity_but_does_not_silence() {
        let (_, full) = peer_target_gains(
            0.0,
            0.0,
            3.0,
            0.0,
            1.0,
            false,
            6.0,
            FalloffMode::Linear,
            0,
            1.0,
        );
        let (_, occluded) = peer_target_gains(
            0.0,
            0.0,
            3.0,
            0.0,
            1.0,
            false,
            6.0,
            FalloffMode::Linear,
            0,
            0.7,
        );
        approx(occluded, full * 0.7);
        assert!(occluded > 0.0 && occluded < full);

        let (_, radio_open) = peer_target_gains(
            0.0,
            0.0,
            3.0,
            0.0,
            1.0,
            false,
            6.0,
            FalloffMode::Linear,
            ROLE_FORCE_AUDIBLE,
            1.0,
        );
        let (_, radio_occluded) = peer_target_gains(
            0.0,
            0.0,
            3.0,
            0.0,
            1.0,
            false,
            6.0,
            FalloffMode::Linear,
            ROLE_FORCE_AUDIBLE,
            0.3,
        );
        approx(radio_occluded, radio_open);
    }

    fn gs_with(
        local: LocalState,
        max_distance: f32,
        falloff: FalloffMode,
        peers: Vec<(&str, PeerState)>,
    ) -> GameState {
        let gs = GameState::new();
        gs.set_local(local);
        gs.set_settings(1.0, max_distance, falloff);
        for (id, p) in peers {
            gs.upsert_peer(id.to_string(), p);
        }
        gs
    }

    #[test]
    fn mix_applies_target_gain_end_to_end() {
        let gs = gs_with(
            LocalState {
                x: 0.0,
                y: 0.0,
                facing: 0.0,
                deafened: false,
            },
            6.0,
            FalloffMode::Linear,
            vec![(
                "p",
                PeerState {
                    x: 3.0,
                    y: 0.0,
                    muted: false,
                    volume: 1.0,
                    role_flags: 0,
                    mode: 0,
                    nvol: 1.0,
                },
            )],
        );
        let mut mixer = Mixer::new();
        let mono = vec![0.1f32; 960];
        let per_peer = vec![("p".to_string(), mono.as_slice())];
        let mut out = vec![0.0f32; 1920];
        mixer.mix(&per_peer, &gs, &mut out);
        approx(out[0], 0.0125);
        approx(out[1], 0.05);
    }

    #[test]
    fn deafened_produces_silence() {
        let gs = gs_with(
            LocalState {
                x: 0.0,
                y: 0.0,
                facing: 0.0,
                deafened: false,
            },
            6.0,
            FalloffMode::Linear,
            vec![(
                "p",
                PeerState {
                    x: 1.0,
                    y: 0.0,
                    muted: false,
                    volume: 1.0,
                    role_flags: 0,
                    mode: 0,
                    nvol: 1.0,
                },
            )],
        );
        let mut mixer = Mixer::new();
        mixer.set_deafened(true);
        let mono = vec![0.5f32; 960];
        let per_peer = vec![("p".to_string(), mono.as_slice())];
        let mut out = vec![1.0f32; 1920];
        mixer.mix(&per_peer, &gs, &mut out);
        assert!(out.iter().all(|&s| s == 0.0));
    }

    #[test]
    fn master_volume_scales_output() {
        let gs = gs_with(
            LocalState {
                x: 0.0,
                y: 0.0,
                facing: 0.0,
                deafened: false,
            },
            6.0,
            FalloffMode::Linear,
            vec![(
                "p",
                PeerState {
                    x: 3.0,
                    y: 0.0,
                    muted: false,
                    volume: 1.0,
                    role_flags: 0,
                    mode: 0,
                    nvol: 1.0,
                },
            )],
        );
        let mut mixer = Mixer::new();
        mixer.set_master(0.5);
        let mono = vec![0.1f32; 960];
        let per_peer = vec![("p".to_string(), mono.as_slice())];
        let mut out = vec![0.0f32; 1920];
        mixer.mix(&per_peer, &gs, &mut out);
        approx(out[0], 0.00625);
        approx(out[1], 0.025);
    }

    #[test]
    fn limiter_and_clamp_keep_output_in_range() {
        let gs = gs_with(
            LocalState {
                x: 0.0,
                y: 0.0,
                facing: 0.0,
                deafened: false,
            },
            6.0,
            FalloffMode::Linear,
            vec![(
                "p",
                PeerState {
                    x: 0.0,
                    y: 0.0,
                    muted: false,
                    volume: 1.0,
                    role_flags: 0,
                    mode: 0,
                    nvol: 1.0,
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
