use std::collections::HashMap;

use crate::gamestate::GameState;

pub const LOW_VOLUME_FLOOR: f32 = 0.06;
pub const GAIN_GLIDE_K: f32 = 0.002;
pub const PLAYBACK_MIX_PEAK_CEILING: f32 = 0.92;
pub const PLAYBACK_MIX_LIMITER_RELEASE_PER_FRAME: f32 = 0.05;
const PAN_FAR_SIDE: f32 = 0.25;

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
    let far_gain = PAN_FAR_SIDE
        + (1.0 - PAN_FAR_SIDE) * (pan.abs() * (std::f32::consts::PI / 2.0)).cos();
    let left = if pan > 0.0 { far_gain } else { 1.0 };
    let right = if pan < 0.0 { far_gain } else { 1.0 };
    (left, right)
}

pub fn peer_target_gains(
    local_x: f32,
    local_y: f32,
    peer_x: f32,
    peer_y: f32,
    peer_volume: f32,
    muted: bool,
    max_distance: f32,
    falloff: FalloffMode,
) -> (f32, f32) {
    if muted {
        return (0.0, 0.0);
    }
    let dx = peer_x - local_x;
    let dy = peer_y - local_y;
    let dist = (dx * dx + dy * dy).sqrt();
    let mut vol = apply_falloff(dist, max_distance, falloff);
    if vol < LOW_VOLUME_FLOOR {
        vol = 0.0;
    }
    let pan = get_pan(local_x, peer_x);
    let (lg, rg) = pan_gains(pan);
    (lg * vol * peer_volume, rg * vol * peer_volume)
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

struct Glide {
    left: f32,
    right: f32,
}

pub struct Mixer {
    master: f32,
    deafened: bool,
    limiter_gain: f32,
    glide: HashMap<String, Glide>,
}

impl Mixer {
    pub fn new() -> Mixer {
        Mixer {
            master: 1.0,
            deafened: false,
            limiter_gain: 1.0,
            glide: HashMap::new(),
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
        for (peer_id, mono) in per_peer {
            let target = match snap.peers.get(peer_id) {
                Some(p) => peer_target_gains(
                    snap.local.x,
                    snap.local.y,
                    p.x,
                    p.y,
                    p.volume,
                    p.muted,
                    snap.max_distance,
                    snap.falloff,
                ),
                None => (0.0, 0.0),
            };
            let g = self.glide.entry(peer_id.clone()).or_insert(Glide {
                left: target.0,
                right: target.1,
            });
            let n = frames.min(mono.len());
            for f in 0..n {
                let s = mono[f];
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
            self.limiter_gain = 1.0_f32.min(self.limiter_gain + PLAYBACK_MIX_LIMITER_RELEASE_PER_FRAME);
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
            .retain(|k, _| per_peer.iter().any(|(pid, _)| pid == k));
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
        approx_tol(apply_falloff(3.0, 6.0, FalloffMode::VoiceFocused), 0.701741, 1e-3);
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
        let (l, r) = peer_target_gains(0.0, 0.0, 3.0, 0.0, 1.0, false, 6.0, FalloffMode::Linear);
        approx(l, 0.125);
        approx(r, 0.5);

        let (l, r) = peer_target_gains(0.0, 0.0, 3.0, 0.0, 2.0, false, 6.0, FalloffMode::Linear);
        approx(l, 0.25);
        approx(r, 1.0);

        let (l, r) = peer_target_gains(0.0, 0.0, 3.0, 0.0, 1.0, true, 6.0, FalloffMode::Linear);
        approx(l, 0.0);
        approx(r, 0.0);

        let (l, r) = peer_target_gains(0.0, 0.0, 5.9, 0.0, 1.0, false, 6.0, FalloffMode::Linear);
        approx(l, 0.0);
        approx(r, 0.0);
    }

    fn gs_with(local: LocalState, max_distance: f32, falloff: FalloffMode, peers: Vec<(&str, PeerState)>) -> GameState {
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
            LocalState { x: 0.0, y: 0.0, facing: 0.0, deafened: false },
            6.0,
            FalloffMode::Linear,
            vec![("p", PeerState { x: 3.0, y: 0.0, muted: false, volume: 1.0, role_flags: 0 })],
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
            LocalState { x: 0.0, y: 0.0, facing: 0.0, deafened: false },
            6.0,
            FalloffMode::Linear,
            vec![("p", PeerState { x: 1.0, y: 0.0, muted: false, volume: 1.0, role_flags: 0 })],
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
            LocalState { x: 0.0, y: 0.0, facing: 0.0, deafened: false },
            6.0,
            FalloffMode::Linear,
            vec![("p", PeerState { x: 3.0, y: 0.0, muted: false, volume: 1.0, role_flags: 0 })],
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
            LocalState { x: 0.0, y: 0.0, facing: 0.0, deafened: false },
            6.0,
            FalloffMode::Linear,
            vec![("p", PeerState { x: 0.0, y: 0.0, muted: false, volume: 1.0, role_flags: 0 })],
        );
        let mut mixer = Mixer::new();
        let mono = vec![1.0f32; 960];
        let per_peer = vec![("p".to_string(), mono.as_slice())];
        let mut out = vec![0.0f32; 1920];
        mixer.mix(&per_peer, &gs, &mut out);
        assert!(out.iter().all(|&s| (-1.0..=1.0).contains(&s)));
    }
}
