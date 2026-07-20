use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

use crate::gamestate::GameState;

pub const GAIN_GLIDE_K: f32 = 0.002;
pub const PLAYBACK_SOFT_LIMIT_START: f32 = 0.92;
const PAN_FAR_SIDE: f32 = 0.25;

const RADIO_DRIVE: f32 = 2.0;
const RADIO_LEVEL: f32 = 0.75;
const WALL_DRY: f32 = 0.85;
const WALL_WET: f32 = 0.12;
const GHOST_DRY: f32 = 0.6;
const GHOST_WET: f32 = 0.08;
const GHOST_COMBS: [usize; 4] = [1214, 1293, 1390, 1476];
const GHOST_ALLPASS: [usize; 2] = [605, 480];
const WALL_COMBS: [usize; 4] = [397, 439, 491, 547];
const WALL_ALLPASS: [usize; 2] = [185, 141];
const GHOST_TAIL_SAMPLES: usize = crate::codec::SAMPLE_RATE as usize * 2;
const WALL_TAIL_SAMPLES: usize = crate::codec::SAMPLE_RATE as usize;
const FILTER_TRANSITION_SAMPLES: usize = 96; // 2 ms at 48 kHz.

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

fn soft_limit_sample(sample: f32) -> f32 {
    if !sample.is_finite() {
        return 0.0;
    }

    let magnitude = sample.abs();
    if magnitude <= PLAYBACK_SOFT_LIMIT_START {
        return sample;
    }

    // A smooth, monotonic knee protects the output without frame-wide normalization. The old
    // reciprocal peak limiter reduced every boosted frame back to exactly 0.92, which made the
    // 100-200% portions of master/per-player volume controls sound identical on hot speech.
    let headroom = 1.0 - PLAYBACK_SOFT_LIMIT_START;
    let limited = PLAYBACK_SOFT_LIMIT_START
        + headroom * ((magnitude - PLAYBACK_SOFT_LIMIT_START) / headroom).tanh();
    sample.signum() * limited.min(1.0)
}

fn soft_limit_stereo_pair(left: f32, right: f32) -> (f32, f32) {
    let left = if left.is_finite() { left } else { 0.0 };
    let right = if right.is_finite() { right } else { 0.0 };
    let peak = left.abs().max(right.abs());
    if peak <= PLAYBACK_SOFT_LIMIT_START {
        return (left, right);
    }

    // Link both channels to the louder side. Independent soft-clipping narrows hard-panned
    // voices under boost because the near channel compresses more than the far channel.
    let limited_peak = soft_limit_sample(peak);
    let gain = limited_peak / peak;
    (
        (left * gain).clamp(-1.0, 1.0),
        (right * gain).clamp(-1.0, 1.0),
    )
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
        FilterMode::WallMuffle | FilterMode::ListenerMuffle => lp650.process(z1, z2, s),
        FilterMode::Ghost => s,
        FilterMode::None => s,
    }
}

fn add_routed_sample(
    mode: FilterMode,
    frame: usize,
    left: f32,
    right: f32,
    out_stereo: &mut [f32],
    ghost_send: &mut [f32],
    wall_send: &mut [f32],
) {
    match mode {
        FilterMode::Ghost => {
            ghost_send[2 * frame] += left;
            ghost_send[2 * frame + 1] += right;
        }
        FilterMode::WallMuffle | FilterMode::ListenerMuffle => {
            wall_send[2 * frame] += left;
            wall_send[2 * frame + 1] += right;
        }
        _ => {
            out_stereo[2 * frame] += left;
            out_stereo[2 * frame + 1] += right;
        }
    }
}

struct Reverb {
    feedback: f32,
    comb_l: Vec<Vec<f32>>,
    comb_r: Vec<Vec<f32>>,
    ci_l: Vec<usize>,
    ci_r: Vec<usize>,
    filt_l: Vec<f32>,
    filt_r: Vec<f32>,
    ap_l: Vec<Vec<f32>>,
    ap_r: Vec<Vec<f32>>,
    ai_l: Vec<usize>,
    ai_r: Vec<usize>,
}

impl Reverb {
    const DAMP1: f32 = 0.2;
    const DAMP2: f32 = 0.8;
    const AP_FEEDBACK: f32 = 0.5;
    const IN_GAIN: f32 = 0.5;

    fn new(comb_len: &[usize], ap_len: &[usize], feedback: f32, spread: usize) -> Reverb {
        Reverb {
            feedback,
            comb_l: comb_len.iter().map(|&n| vec![0.0; n]).collect(),
            comb_r: comb_len.iter().map(|&n| vec![0.0; n + spread]).collect(),
            ci_l: vec![0; comb_len.len()],
            ci_r: vec![0; comb_len.len()],
            filt_l: vec![0.0; comb_len.len()],
            filt_r: vec![0.0; comb_len.len()],
            ap_l: ap_len.iter().map(|&n| vec![0.0; n]).collect(),
            ap_r: ap_len.iter().map(|&n| vec![0.0; n + spread]).collect(),
            ai_l: vec![0; ap_len.len()],
            ai_r: vec![0; ap_len.len()],
        }
    }

    fn comb(buf: &mut [f32], idx: &mut usize, store: &mut f32, input: f32, feedback: f32) -> f32 {
        let y = buf[*idx];
        *store = y * Reverb::DAMP2 + *store * Reverb::DAMP1;
        buf[*idx] = input + *store * feedback;
        *idx += 1;
        if *idx >= buf.len() {
            *idx = 0;
        }
        y
    }

    fn allpass(buf: &mut [f32], idx: &mut usize, input: f32) -> f32 {
        let y = buf[*idx];
        let output = y - input;
        buf[*idx] = input + y * Reverb::AP_FEEDBACK;
        *idx += 1;
        if *idx >= buf.len() {
            *idx = 0;
        }
        output
    }

    fn process(&mut self, input: f32) -> (f32, f32) {
        let x = input * Reverb::IN_GAIN;
        let mut l = 0.0;
        let mut r = 0.0;
        for i in 0..self.comb_l.len() {
            l += Reverb::comb(
                &mut self.comb_l[i],
                &mut self.ci_l[i],
                &mut self.filt_l[i],
                x,
                self.feedback,
            );
            r += Reverb::comb(
                &mut self.comb_r[i],
                &mut self.ci_r[i],
                &mut self.filt_r[i],
                x,
                self.feedback,
            );
        }
        for i in 0..self.ap_l.len() {
            l = Reverb::allpass(&mut self.ap_l[i], &mut self.ai_l[i], l);
            r = Reverb::allpass(&mut self.ap_r[i], &mut self.ai_r[i], r);
        }
        (l, r)
    }

    fn reset(&mut self) {
        for b in self.comb_l.iter_mut() {
            b.iter_mut().for_each(|s| *s = 0.0);
        }
        for b in self.comb_r.iter_mut() {
            b.iter_mut().for_each(|s| *s = 0.0);
        }
        for b in self.ap_l.iter_mut() {
            b.iter_mut().for_each(|s| *s = 0.0);
        }
        for b in self.ap_r.iter_mut() {
            b.iter_mut().for_each(|s| *s = 0.0);
        }
        self.ci_l.iter_mut().for_each(|i| *i = 0);
        self.ci_r.iter_mut().for_each(|i| *i = 0);
        self.ai_l.iter_mut().for_each(|i| *i = 0);
        self.ai_r.iter_mut().for_each(|i| *i = 0);
        self.filt_l.iter_mut().for_each(|s| *s = 0.0);
        self.filt_r.iter_mut().for_each(|s| *s = 0.0);
    }
}

struct Glide {
    left: f32,
    right: f32,
    mode: FilterMode,
    bz1: f32,
    bz2: f32,
    previous_mode: FilterMode,
    previous_bz1: f32,
    previous_bz2: f32,
    transition_remaining: usize,
}

pub struct Mixer {
    glide: HashMap<String, Glide>,
    lp650: Biquad,
    hp650: Biquad,
    lp1900: Biquad,
    ghost_reverb: Reverb,
    wall_reverb: Reverb,
    ghost_send: Vec<f32>,
    wall_send: Vec<f32>,
    ghost_lp_z1: f32,
    ghost_lp_z2: f32,
    ghost_tail: usize,
    wall_tail: usize,
    observed_deafen_epoch: u64,
    had_input_last_round: bool,
}

impl Default for Mixer {
    fn default() -> Self {
        Self::new()
    }
}

impl Mixer {
    pub fn new() -> Mixer {
        Mixer {
            glide: HashMap::new(),
            lp650: Biquad::lowpass(650.0, 0.7),
            hp650: Biquad::highpass(650.0, 0.9),
            lp1900: Biquad::lowpass(1900.0, 0.7),
            ghost_reverb: Reverb::new(&GHOST_COMBS, &GHOST_ALLPASS, 0.82, 25),
            wall_reverb: Reverb::new(&WALL_COMBS, &WALL_ALLPASS, 0.6, 11),
            ghost_send: Vec::new(),
            wall_send: Vec::new(),
            ghost_lp_z1: 0.0,
            ghost_lp_z2: 0.0,
            ghost_tail: 0,
            wall_tail: 0,
            observed_deafen_epoch: 0,
            had_input_last_round: false,
        }
    }

    fn reset_effect_state(&mut self) {
        // Treat deafen as an immediate break in the playback signal path. Otherwise reverb and
        // filter history accumulated before deafen can resume audibly when playback is restored.
        self.glide.clear();
        self.ghost_reverb.reset();
        self.wall_reverb.reset();
        self.ghost_send.fill(0.0);
        self.wall_send.fill(0.0);
        self.ghost_lp_z1 = 0.0;
        self.ghost_lp_z2 = 0.0;
        self.ghost_tail = 0;
        self.wall_tail = 0;
        self.had_input_last_round = false;
    }

    /// True while one empty 20 ms mix is needed to flush per-peer filter state or while an
    /// existing reverb tail still has samples to render.
    pub fn needs_idle_mix(&self) -> bool {
        self.had_input_last_round || self.ghost_tail > 0 || self.wall_tail > 0
    }

    pub fn mix(&mut self, per_peer: &[(String, &[f32])], gs: &GameState, out_stereo: &mut [f32]) {
        for s in out_stereo.iter_mut() {
            *s = 0.0;
        }
        let snap = gs.snapshot();
        if self.observed_deafen_epoch != snap.deafen_epoch {
            self.reset_effect_state();
            self.observed_deafen_epoch = snap.deafen_epoch;
        }
        if snap.local.deafened {
            self.had_input_last_round = false;
            return;
        }
        let frames = out_stereo.len() / 2;
        if self.ghost_send.len() != out_stereo.len() {
            self.ghost_send = vec![0.0; out_stereo.len()];
            self.wall_send = vec![0.0; out_stereo.len()];
        } else {
            for s in self.ghost_send.iter_mut() {
                *s = 0.0;
            }
            for s in self.wall_send.iter_mut() {
                *s = 0.0;
            }
        }
        let (lp650, hp650) = (self.lp650, self.hp650);
        let mut any_ghost = false;
        let mut any_wall = false;
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
                previous_mode: mode,
                previous_bz1: 0.0,
                previous_bz2: 0.0,
                transition_remaining: 0,
            });
            if g.mode != mode {
                g.previous_mode = g.mode;
                g.previous_bz1 = g.bz1;
                g.previous_bz2 = g.bz2;
                g.transition_remaining = FILTER_TRANSITION_SAMPLES;
                g.mode = mode;
                g.bz1 = 0.0;
                g.bz2 = 0.0;
            }
            let transition_mode = (g.transition_remaining > 0).then_some(g.previous_mode);
            for active_mode in [Some(mode), transition_mode].into_iter().flatten() {
                match active_mode {
                    FilterMode::Ghost => any_ghost = true,
                    FilterMode::WallMuffle | FilterMode::ListenerMuffle => any_wall = true,
                    _ => {}
                }
            }
            let n = frames.min(mono.len());
            for f in 0..n {
                let current = apply_filter(mode, &lp650, &hp650, &mut g.bz1, &mut g.bz2, mono[f]);
                g.left += GAIN_GLIDE_K * (target.0 - g.left);
                g.right += GAIN_GLIDE_K * (target.1 - g.right);
                if g.transition_remaining == 0 {
                    add_routed_sample(
                        mode,
                        f,
                        current * g.left,
                        current * g.right,
                        out_stereo,
                        &mut self.ghost_send,
                        &mut self.wall_send,
                    );
                    continue;
                }

                let previous = apply_filter(
                    g.previous_mode,
                    &lp650,
                    &hp650,
                    &mut g.previous_bz1,
                    &mut g.previous_bz2,
                    mono[f],
                );
                let completed = FILTER_TRANSITION_SAMPLES - g.transition_remaining;
                let progress = if FILTER_TRANSITION_SAMPLES <= 1 {
                    1.0
                } else {
                    completed as f32 / (FILTER_TRANSITION_SAMPLES - 1) as f32
                };
                add_routed_sample(
                    g.previous_mode,
                    f,
                    previous * g.left * (1.0 - progress),
                    previous * g.right * (1.0 - progress),
                    out_stereo,
                    &mut self.ghost_send,
                    &mut self.wall_send,
                );
                add_routed_sample(
                    mode,
                    f,
                    current * g.left * progress,
                    current * g.right * progress,
                    out_stereo,
                    &mut self.ghost_send,
                    &mut self.wall_send,
                );
                g.transition_remaining -= 1;
            }
        }

        // Ghost reverb: band-limit the send, run the reverb, add GhostDry*dry + GhostWet*wet.
        // A tail keeps the reverb ringing for ~2s after the last ghost peer, then it is reset.
        if any_ghost {
            self.ghost_tail = GHOST_TAIL_SAMPLES;
        }
        if any_ghost || self.ghost_tail > 0 {
            let lp1900 = self.lp1900;
            for f in 0..frames {
                let l = self.ghost_send[2 * f];
                let r = self.ghost_send[2 * f + 1];
                let mono =
                    lp1900.process(&mut self.ghost_lp_z1, &mut self.ghost_lp_z2, (l + r) * 0.5);
                let (wl, wr) = self.ghost_reverb.process(mono);
                out_stereo[2 * f] += GHOST_DRY * l + GHOST_WET * wl;
                out_stereo[2 * f + 1] += GHOST_DRY * r + GHOST_WET * wr;
            }
            if !any_ghost {
                self.ghost_tail = self.ghost_tail.saturating_sub(frames);
                if self.ghost_tail == 0 {
                    self.ghost_reverb.reset();
                    self.ghost_lp_z1 = 0.0;
                    self.ghost_lp_z2 = 0.0;
                }
            }
        }

        // Wall/occlusion reverb: the send is already 650Hz low-passed per peer; add the small
        // room tail. ~1s tail after the last occluded peer.
        if any_wall {
            self.wall_tail = WALL_TAIL_SAMPLES;
        }
        if any_wall || self.wall_tail > 0 {
            for f in 0..frames {
                let l = self.wall_send[2 * f];
                let r = self.wall_send[2 * f + 1];
                let (wl, wr) = self.wall_reverb.process((l + r) * 0.5);
                out_stereo[2 * f] += WALL_DRY * l + WALL_WET * wl;
                out_stereo[2 * f + 1] += WALL_DRY * r + WALL_WET * wr;
            }
            if !any_wall {
                self.wall_tail = self.wall_tail.saturating_sub(frames);
                if self.wall_tail == 0 {
                    self.wall_reverb.reset();
                }
            }
        }

        let master = snap.master.clamp(0.0, 2.0);
        if master != 1.0 {
            for s in out_stereo.iter_mut() {
                *s *= master;
            }
        }

        let mut sample_index = 0;
        while sample_index + 1 < out_stereo.len() {
            let (left, right) =
                soft_limit_stereo_pair(out_stereo[sample_index], out_stereo[sample_index + 1]);
            out_stereo[sample_index] = left;
            out_stereo[sample_index + 1] = right;
            sample_index += 2;
        }
        if sample_index < out_stereo.len() {
            out_stereo[sample_index] = soft_limit_sample(out_stereo[sample_index]);
        }

        for (peer, glide) in self.glide.iter_mut() {
            if !per_peer.iter().any(|(present, _)| present == peer) {
                glide.bz1 = 0.0;
                glide.bz2 = 0.0;
                glide.previous_bz1 = 0.0;
                glide.previous_bz2 = 0.0;
                glide.transition_remaining = 0;
            }
        }
        self.had_input_last_round = !per_peer.is_empty();

        self.glide
            .retain(|k, _| snap.peers.contains_key(k) || per_peer.iter().any(|(pid, _)| pid == k));
    }
}

pub const JITTER_PRIME_FRAMES: usize = 2;
pub const JITTER_CAP_FRAMES: usize = 12;
const JITTER_FRAME_DURATION: Duration = Duration::from_millis(20);
const JITTER_TALKSPURT_RESET: Duration = Duration::from_millis(250);
const JITTER_UNDERRUN_REBUFFER: Duration = Duration::from_millis(40);
const JITTER_SAME_DRAIN_THRESHOLD: Duration = Duration::from_millis(2);
const JITTER_EWMA_GAIN: f64 = 1.0 / 16.0;
const JITTER_TARGET_GAIN: f64 = 2.5;
const JITTER_STABLE_ARRIVALS_TO_DECAY: usize = 250;
const JITTER_QUIET_PEAK: f32 = 0.003;
const JITTER_QUIET_RMS: f32 = 0.001;
// Concealment may decode as many as five missing 20 ms frames together with the live frame.  A
// plain FIFO would replay that whole batch at 1x and permanently move the talkspurt 20-100 ms
// behind real time.  Consume at most ten percent extra PCM per playout round and remove it with a
// correlation-selected overlap, which bounds catch-up to one second for the largest supported gap
// without dropping an entire voiced frame or changing the output cadence.
const JITTER_CATCHUP_MAX_RATE_DIVISOR: usize = 10;
const JITTER_CATCHUP_OVERLAP_DIVISOR: usize = 15;
const JITTER_CATCHUP_SEARCH_STEP: usize = 8;
const JITTER_TAIL_FADE_SAMPLES: usize = 48;

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct DecodedPlayoutSnapshot {
    pub recovery_batches: u64,
    pub recovery_frames: u64,
    pub catchup_rounds: u64,
    pub catchup_samples: u64,
    pub overflow_evicted_frames: u64,
    pub overflow_evicted_samples: u64,
}

#[derive(Default)]
struct DecodedPlayoutMetrics {
    recovery_batches: AtomicU64,
    recovery_frames: AtomicU64,
    catchup_rounds: AtomicU64,
    catchup_samples: AtomicU64,
    overflow_evicted_frames: AtomicU64,
    overflow_evicted_samples: AtomicU64,
}

impl DecodedPlayoutMetrics {
    fn snapshot(&self) -> DecodedPlayoutSnapshot {
        DecodedPlayoutSnapshot {
            recovery_batches: self.recovery_batches.load(Ordering::Relaxed),
            recovery_frames: self.recovery_frames.load(Ordering::Relaxed),
            catchup_rounds: self.catchup_rounds.load(Ordering::Relaxed),
            catchup_samples: self.catchup_samples.load(Ordering::Relaxed),
            overflow_evicted_frames: self.overflow_evicted_frames.load(Ordering::Relaxed),
            overflow_evicted_samples: self.overflow_evicted_samples.load(Ordering::Relaxed),
        }
    }
}

struct PeerQueue {
    frames: VecDeque<Vec<f32>>,
    front_offset: usize,
    frame_samples: usize,
    recovery_debt_samples: usize,
    primed: bool,
    target: usize,
    last_arrival: Option<Instant>,
    jitter_frames: f64,
    stable_arrivals: usize,
    shed_quiet_frame: bool,
    last_output_sample: f32,
    has_rendered: bool,
    transition_from: Option<f32>,
}

impl PeerQueue {
    fn new(base_prime: usize) -> Self {
        Self {
            frames: VecDeque::new(),
            front_offset: 0,
            frame_samples: 0,
            recovery_debt_samples: 0,
            primed: false,
            target: base_prime,
            last_arrival: None,
            jitter_frames: 0.0,
            stable_arrivals: 0,
            shed_quiet_frame: false,
            last_output_sample: 0.0,
            has_rendered: false,
            transition_from: None,
        }
    }

    fn reset_playout(&mut self) {
        if self.has_rendered {
            self.transition_from = Some(self.last_output_sample);
            self.has_rendered = false;
        }
        self.frames.clear();
        self.front_offset = 0;
        self.recovery_debt_samples = 0;
        self.primed = false;
        self.shed_quiet_frame = false;
    }

    fn available_samples(&self) -> usize {
        self.frames
            .iter()
            .map(Vec::len)
            .sum::<usize>()
            .saturating_sub(self.front_offset)
    }

    fn front_samples(&self) -> Option<&[f32]> {
        self.frames
            .front()
            .map(|frame| &frame[self.front_offset.min(frame.len())..])
    }

    fn copy_prefix(&self, count: usize) -> Vec<f32> {
        let mut output = Vec::with_capacity(count);
        for (index, frame) in self.frames.iter().enumerate() {
            let start = if index == 0 { self.front_offset } else { 0 };
            if start >= frame.len() {
                continue;
            }
            let take = (count - output.len()).min(frame.len() - start);
            output.extend_from_slice(&frame[start..start + take]);
            if output.len() == count {
                break;
            }
        }
        output
    }

    fn consume_prefix(&mut self, mut count: usize) {
        while count > 0 {
            let Some(front) = self.frames.front() else {
                self.front_offset = 0;
                return;
            };
            let remaining = front.len().saturating_sub(self.front_offset);
            if count < remaining {
                self.front_offset += count;
                return;
            }
            count = count.saturating_sub(remaining);
            self.frames.pop_front();
            self.front_offset = 0;
        }
    }

    fn drop_front_frame(&mut self) -> usize {
        let removed = self
            .frames
            .pop_front()
            .map_or(0, |frame| frame.len().saturating_sub(self.front_offset));
        self.front_offset = 0;
        removed
    }
}

pub struct PeerJitter {
    peers: HashMap<String, PeerQueue>,
    prime: usize,
    cap: usize,
    adaptive: bool,
    metrics: DecodedPlayoutMetrics,
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
        Self::with_mode(prime, cap, true)
    }

    /// Constructs the decoded staging layer used after the adaptive encoded jitter buffer.  It
    /// deliberately does not learn a second network-latency target; recovery batches are instead
    /// returned to the one-frame target with bounded overlap-add catch-up.
    pub fn with_staging_limits(prime: usize, cap: usize) -> PeerJitter {
        Self::with_mode(prime, cap, false)
    }

    fn with_mode(prime: usize, cap: usize, adaptive: bool) -> PeerJitter {
        let prime = prime.max(1);
        PeerJitter {
            peers: HashMap::new(),
            prime,
            cap: cap.max(prime),
            adaptive,
            metrics: DecodedPlayoutMetrics::default(),
        }
    }

    pub fn push(&mut self, peer: &str, frame: Vec<f32>) {
        self.push_at(peer, frame, Instant::now());
    }

    /// Enqueues one decoder drain atomically from the playout layer's point of view.  `recovered`
    /// is the number of leading DRED/FEC/PLC frames; the final frame is normally the live RTP
    /// packet.  Recording exact recovered sample debt lets playout catch up only the time that was
    /// reconstructed, rather than treating ordinary encoded jitter-buffer depth as stale audio.
    pub fn push_batch(&mut self, peer: &str, frames: Vec<Vec<f32>>, recovered: usize) {
        self.push_batch_at(peer, frames, recovered, Instant::now());
    }

    fn push_batch_at(&mut self, peer: &str, frames: Vec<Vec<f32>>, recovered: usize, now: Instant) {
        if frames.is_empty() {
            return;
        }
        let recovered = recovered.min(frames.len());
        let recovered_samples = frames.iter().take(recovered).map(Vec::len).sum::<usize>();

        // Every decoded frame in one drain has the same network arrival.  Reusing one timestamp
        // keeps the arrival estimator from mistaking FEC/PLC frames for a zero-millisecond burst.
        for frame in frames {
            self.push_at(peer, frame, now);
        }

        if recovered_samples > 0 {
            self.metrics
                .recovery_batches
                .fetch_add(1, Ordering::Relaxed);
            self.metrics
                .recovery_frames
                .fetch_add(recovered as u64, Ordering::Relaxed);

            // Credit debt only after format/talkspurt resets and hard-cap eviction have settled.
            // Never credit more than the retained excess above one live playout frame, otherwise
            // a dropped/empty current decode could leave stale debt after the queue drains.
            if let Some(q) = self.peers.get_mut(peer) {
                let retained_excess = q.available_samples().saturating_sub(q.frame_samples);
                let credit_capacity = retained_excess.saturating_sub(q.recovery_debt_samples);
                let credited = recovered_samples.min(credit_capacity);
                q.recovery_debt_samples = q.recovery_debt_samples.saturating_add(credited);
            }
        }
    }

    fn push_at(&mut self, peer: &str, frame: Vec<f32>, now: Instant) {
        if frame.is_empty() {
            return;
        }
        let base_prime = self.prime;
        let cap = self.cap;
        let adaptive = self.adaptive;
        let q = self
            .peers
            .entry(peer.to_string())
            .or_insert_with(|| PeerQueue::new(base_prime));

        // A decoder format change cannot be spliced into an old partial frame safely.  Opus is
        // fixed at 960 samples in production, but resetting here keeps the queue fail-safe if a
        // future decoder mode changes frame duration.
        if q.frame_samples != 0 && q.frame_samples != frame.len() {
            q.reset_playout();
        }
        q.frame_samples = frame.len();

        let interval = q
            .last_arrival
            .map(|previous| now.saturating_duration_since(previous));
        let was_empty = q.frames.is_empty();
        if adaptive {
            Self::observe_arrival(q, now, base_prime, cap);
        } else {
            q.last_arrival = Some(now);
            q.target = base_prime;
        }

        // A long transport stall or a measured empty-queue underrun starts a new playout
        // generation. The quiet-frame hold in playout_round grows a live buffer without freezing
        // speech; this reset is the fallback when no quiet audio is available.
        if interval.is_some_and(|elapsed| elapsed >= JITTER_TALKSPURT_RESET) {
            q.reset_playout();
            q.frame_samples = frame.len();
        } else if q.primed
            && was_empty
            && interval.is_some_and(|elapsed| elapsed >= JITTER_UNDERRUN_REBUFFER)
        {
            q.primed = false;
            q.transition_from = Some(q.last_output_sample);
            q.has_rendered = false;
        }
        if q.frames.len() >= cap {
            if let Some(evicted) = q.frames.pop_front() {
                let evicted_samples = evicted.len().saturating_sub(q.front_offset);
                q.front_offset = 0;
                q.recovery_debt_samples = q.recovery_debt_samples.saturating_sub(evicted_samples);
                q.transition_from = Some(q.last_output_sample);
                self.metrics
                    .overflow_evicted_frames
                    .fetch_add(1, Ordering::Relaxed);
                self.metrics
                    .overflow_evicted_samples
                    .fetch_add(evicted_samples as u64, Ordering::Relaxed);
            }
        }
        q.frames.push_back(frame);
    }

    fn observe_arrival(q: &mut PeerQueue, now: Instant, base_prime: usize, cap: usize) {
        let Some(previous) = q.last_arrival else {
            q.last_arrival = Some(now);
            return;
        };
        let interval = now.saturating_duration_since(previous);

        // A decoder drain may enqueue PLC/FEC plus the live frame back-to-back. Those are one
        // network arrival, not zero-millisecond packet jitter, so leave the timestamp anchored
        // to the first frame in the drain. Likewise, a long mute/talkspurt gap starts a fresh
        // measurement instead of permanently increasing the next utterance's latency.
        if interval <= JITTER_SAME_DRAIN_THRESHOLD {
            return;
        }
        q.last_arrival = Some(now);
        if interval >= JITTER_TALKSPURT_RESET {
            q.stable_arrivals = 0;
            return;
        }

        let interval_frames = interval.as_secs_f64() / JITTER_FRAME_DURATION.as_secs_f64();
        let deviation = (interval_frames - 1.0).abs();
        q.jitter_frames += (deviation - q.jitter_frames) * JITTER_EWMA_GAIN;

        let extra = (q.jitter_frames * JITTER_TARGET_GAIN).round() as usize;
        let desired = base_prime.saturating_add(extra).clamp(base_prime, cap);
        if desired > q.target {
            // React quickly to a worsening route, but recover latency slowly so a short calm
            // window does not make a still-jittery peer oscillate between depths.
            q.target = desired;
            q.stable_arrivals = 0;
            q.shed_quiet_frame = false;
        } else if desired < q.target {
            q.stable_arrivals += 1;
            if q.stable_arrivals >= JITTER_STABLE_ARRIVALS_TO_DECAY {
                q.target -= 1;
                q.stable_arrivals = 0;
                q.shed_quiet_frame = true;
            }
        } else {
            q.stable_arrivals = 0;
        }
    }

    pub fn remove(&mut self, peer: &str) {
        self.peers.remove(peer);
    }

    /// Clears stale decoded PCM while preserving the last rendered boundary for a smooth restart.
    pub fn reset_peer(&mut self, peer: &str) {
        if let Some(queue) = self.peers.get_mut(peer) {
            queue.reset_playout();
        }
    }

    pub fn playout_round(&mut self) -> Vec<(String, Vec<f32>)> {
        let mut out = Vec::new();
        let metrics = &self.metrics;
        for (peer, q) in self.peers.iter_mut() {
            if !q.primed {
                if q.frames.len() >= q.target {
                    q.primed = true;
                } else {
                    continue;
                }
            }

            // Adapt an already-playing stream without clipping words. If the route needs more
            // cushion, hold only decoded near-silence for one round so the queue can grow. When
            // the target later falls, discard at most one quiet surplus frame to shed latency.
            // Voiced frames are never held or dropped; they wait for a real underrun boundary.
            if q.frames.len() < q.target && q.front_samples().is_some_and(is_quiet_frame) {
                continue;
            }
            if q.shed_quiet_frame
                && q.frames.len() > q.target
                && q.front_samples().is_some_and(is_quiet_frame)
            {
                let removed = q.drop_front_frame();
                q.recovery_debt_samples = q.recovery_debt_samples.saturating_sub(removed);
                q.shed_quiet_frame = false;
            }

            let available = q.available_samples();
            let output_samples = q.frame_samples.max(1);
            if available == 0 {
                q.primed = false;
                if q.has_rendered {
                    let frame = fade_from_sample(q.last_output_sample, output_samples);
                    q.last_output_sample = 0.0;
                    q.has_rendered = false;
                    q.transition_from = Some(0.0);
                    out.push((peer.clone(), frame));
                }
                continue;
            }
            if available < output_samples {
                // Catch-up advances through frame boundaries, so a transport stall can leave a
                // partial decoded tail. Never hand a short vector to the fixed-cadence mixer: fade
                // the retained PCM to zero, pad the rest, and retire debt now that no backlog exists.
                let source = q.copy_prefix(available);
                q.consume_prefix(available);
                q.recovery_debt_samples = 0;
                q.primed = false;
                q.shed_quiet_frame = false;
                let mut frame = fade_and_pad_tail(&source, output_samples);
                if let Some(from) = q.transition_from.take() {
                    crossfade_head(&mut frame, from);
                }
                q.last_output_sample = 0.0;
                q.has_rendered = false;
                q.transition_from = Some(0.0);
                out.push((peer.clone(), frame));
                continue;
            }
            let max_extra = (output_samples / JITTER_CATCHUP_MAX_RATE_DIVISOR).max(1);
            let extra = q
                .recovery_debt_samples
                .min(max_extra)
                .min(available.saturating_sub(output_samples));
            let input_samples = output_samples + extra;
            let mut frame = if extra == 0
                && q.front_offset == 0
                && q.frames
                    .front()
                    .is_some_and(|frame| frame.len() == output_samples)
            {
                // Preserve the allocation-free steady-state path. Copying is required only while
                // overlap-add catch-up leaves the read cursor partway through a decoded frame.
                q.frames.pop_front().expect("front frame checked above")
            } else if extra > 0 {
                let source = q.copy_prefix(input_samples);
                debug_assert_eq!(source.len(), input_samples);
                metrics.catchup_rounds.fetch_add(1, Ordering::Relaxed);
                metrics
                    .catchup_samples
                    .fetch_add(extra as u64, Ordering::Relaxed);
                q.recovery_debt_samples -= extra;
                let frame = overlap_accelerate(&source, output_samples, extra);
                q.consume_prefix(input_samples);
                frame
            } else {
                let frame = q.copy_prefix(input_samples);
                debug_assert_eq!(frame.len(), input_samples);
                q.consume_prefix(input_samples);
                frame
            };
            if let Some(from) = q.transition_from.take() {
                crossfade_head(&mut frame, from);
            }
            q.last_output_sample = frame.last().copied().unwrap_or(0.0);
            q.has_rendered = true;
            out.push((peer.clone(), frame));
        }
        out
    }

    pub fn is_idle(&self) -> bool {
        self.peers
            .values()
            .all(|q| q.frames.is_empty() && !q.primed)
    }

    pub fn metrics_snapshot(&self) -> DecodedPlayoutSnapshot {
        self.metrics.snapshot()
    }

    #[cfg(test)]
    fn target_for(&self, peer: &str) -> Option<usize> {
        self.peers.get(peer).map(|q| q.target)
    }

    #[cfg(test)]
    fn primed_for(&self, peer: &str) -> Option<bool> {
        self.peers.get(peer).map(|q| q.primed)
    }

    #[cfg(test)]
    fn depth_samples_for(&self, peer: &str) -> Option<usize> {
        self.peers.get(peer).map(PeerQueue::available_samples)
    }

    #[cfg(test)]
    fn recovery_debt_samples_for(&self, peer: &str) -> Option<usize> {
        self.peers.get(peer).map(|q| q.recovery_debt_samples)
    }
}

/// Removes `extra` samples from a fixed-cadence playout frame without an abrupt whole-frame drop.
/// The best low-error splice in the middle half of the frame is crossfaded with a raised-cosine
/// window.  Samples outside the overlap retain their original rate, so pitch is preserved except
/// for the short transition and consecutive output frames remain exactly the decoder frame size.
fn overlap_accelerate(source: &[f32], output_samples: usize, extra: usize) -> Vec<f32> {
    debug_assert_eq!(source.len(), output_samples + extra);
    if extra == 0 || output_samples < 8 {
        return source[..output_samples].to_vec();
    }

    let overlap = (output_samples / JITTER_CATCHUP_OVERLAP_DIVISOR)
        .clamp(4, 96)
        .min(output_samples.saturating_sub(1));
    let search_start = output_samples / 4;
    let search_end = output_samples
        .saturating_mul(3)
        .checked_div(4)
        .unwrap_or(output_samples)
        .min(output_samples.saturating_sub(overlap));
    let cut = best_overlap_cut(source, extra, overlap, search_start, search_end);

    let mut output = Vec::with_capacity(output_samples);
    output.extend_from_slice(&source[..cut]);
    for index in 0..overlap {
        let phase = (index + 1) as f32 / (overlap + 1) as f32;
        let weight = 0.5 - 0.5 * (std::f32::consts::PI * phase).cos();
        let left = source[cut + index];
        let right = source[cut + extra + index];
        output.push(left + (right - left) * weight);
    }
    output.extend_from_slice(&source[cut + extra + overlap..]);
    debug_assert_eq!(output.len(), output_samples);
    output
}

fn fade_from_sample(from: f32, output_samples: usize) -> Vec<f32> {
    let mut output = vec![0.0; output_samples];
    let fade = output_samples.min(JITTER_TAIL_FADE_SAMPLES);
    if fade == 1 {
        output[0] = 0.0;
    } else if fade > 1 {
        for (index, sample) in output[..fade].iter_mut().enumerate() {
            let phase = index as f32 / (fade - 1) as f32;
            let gain = 0.5 + 0.5 * (std::f32::consts::PI * phase).cos();
            *sample = from * gain;
        }
    }
    output
}

fn crossfade_head(frame: &mut [f32], from: f32) {
    let fade = frame.len().min(JITTER_TAIL_FADE_SAMPLES);
    if fade == 1 {
        return;
    }
    for (index, sample) in frame[..fade].iter_mut().enumerate() {
        let phase = index as f32 / (fade - 1) as f32;
        let weight = 0.5 - 0.5 * (std::f32::consts::PI * phase).cos();
        *sample = from + (*sample - from) * weight;
    }
}

fn fade_and_pad_tail(source: &[f32], output_samples: usize) -> Vec<f32> {
    let copied = source.len().min(output_samples);
    let mut output = vec![0.0; output_samples];
    output[..copied].copy_from_slice(&source[..copied]);
    let fade = copied.min(JITTER_TAIL_FADE_SAMPLES);
    if fade > 0 {
        let start = copied - fade;
        for index in 0..fade {
            let phase = (index + 1) as f32 / fade as f32;
            let gain = 0.5 + 0.5 * (std::f32::consts::PI * phase).cos();
            output[start + index] *= gain;
        }
    }
    output
}

fn best_overlap_cut(
    source: &[f32],
    extra: usize,
    overlap: usize,
    search_start: usize,
    search_end: usize,
) -> usize {
    if search_start >= search_end {
        return search_start.min(source.len().saturating_sub(extra + overlap));
    }
    let mut best_cut = search_start;
    let mut best_error = f64::INFINITY;
    for cut in (search_start..=search_end).step_by(JITTER_CATCHUP_SEARCH_STEP) {
        let mut difference = 0.0f64;
        let mut energy = 1.0e-12f64;
        for index in 0..overlap {
            let left = f64::from(source[cut + index]);
            let right = f64::from(source[cut + extra + index]);
            let delta = left - right;
            difference += delta * delta;
            energy += left * left + right * right;
        }
        let normalized = difference / energy;
        if normalized < best_error {
            best_error = normalized;
            best_cut = cut;
        }
    }
    best_cut
}

fn is_quiet_frame(frame: &[f32]) -> bool {
    if frame.is_empty() {
        return true;
    }
    let mut peak = 0.0f32;
    let mut squares = 0.0f64;
    for &sample in frame {
        peak = peak.max(sample.abs());
        squares += f64::from(sample) * f64::from(sample);
    }
    let rms = (squares / frame.len() as f64).sqrt() as f32;
    peak <= JITTER_QUIET_PEAK && rms <= JITTER_QUIET_RMS
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::codec::{OpusCodec, FRAME_SIZE, SAMPLE_RATE};
    use crate::gamestate::{GameState, LocalState, PeerState};
    use crate::input::InputConfig;

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
        assert!(!jb.is_idle(), "a rendered edge still needs one fade round");
        let fade = jb.playout_round();
        assert_eq!(fade.len(), 1);
        assert_eq!(fade[0].1, vec![0.0]);
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
        assert_eq!(
            jb.metrics_snapshot(),
            DecodedPlayoutSnapshot {
                overflow_evicted_frames: 2,
                overflow_evicted_samples: 2,
                ..DecodedPlayoutSnapshot::default()
            },
            "a hard-cap eviction must be observable rather than silent"
        );
    }

    #[test]
    fn recovered_batch_smoothly_returns_to_live_depth_during_continuous_speech() {
        let mut jb = PeerJitter::with_staging_limits(1, 8);
        let mut phase = 0usize;
        let mut next_frame = || {
            let frame = (0..FRAME_SIZE)
                .map(|sample| {
                    let value = ((phase + sample) as f32 * 0.017).sin() * 0.25;
                    if value.is_finite() {
                        value
                    } else {
                        0.0
                    }
                })
                .collect::<Vec<_>>();
            phase += FRAME_SIZE;
            frame
        };

        // Five concealed frames plus the live packet is the largest supported decoder drain.
        let batch = (0..6).map(|_| next_frame()).collect::<Vec<_>>();
        jb.push_batch("a", batch, 5);
        let first = jb.playout_round();
        assert_eq!(first.len(), 1);
        assert_eq!(first[0].1.len(), FRAME_SIZE);
        assert!(first[0].1.iter().all(|sample| sample.is_finite()));
        assert!(jb.depth_samples_for("a").unwrap() < FRAME_SIZE * 5);

        // At ten-percent acceleration the exact 4,800-sample recovery debt is retired in fifty
        // output rounds.  Normal 20 ms packets continue arriving while catch-up is in progress.
        for _ in 1..50 {
            jb.push("a", next_frame());
            let round = jb.playout_round();
            assert_eq!(round.len(), 1);
            assert_eq!(round[0].1.len(), FRAME_SIZE);
            assert!(round[0].1.iter().all(|sample| sample.is_finite()));
        }
        assert_eq!(jb.recovery_debt_samples_for("a"), Some(0));
        assert_eq!(jb.depth_samples_for("a"), Some(0));
        assert!(
            !jb.is_idle(),
            "the last voiced edge must remain pending until its fade-to-silence round"
        );
        let fade = jb.playout_round();
        assert_eq!(fade.len(), 1);
        assert_eq!(fade[0].1.len(), FRAME_SIZE);
        assert!(fade[0].1.iter().all(|sample| sample.is_finite()));
        assert!(
            jb.is_idle(),
            "recovered history must not remain as permanent latency"
        );

        let metrics = jb.metrics_snapshot();
        assert_eq!(metrics.recovery_batches, 1);
        assert_eq!(metrics.recovery_frames, 5);
        assert_eq!(metrics.catchup_rounds, 50);
        assert_eq!(metrics.catchup_samples, (FRAME_SIZE * 5) as u64);
        assert_eq!(metrics.overflow_evicted_frames, 0);
        assert_eq!(metrics.overflow_evicted_samples, 0);
    }

    #[test]
    fn jitter_crossfades_underflow_and_resume_without_a_hard_edge() {
        let mut jb = PeerJitter::with_staging_limits(1, 8);
        jb.push("a", vec![0.8; FRAME_SIZE]);
        let voice = jb.playout_round();
        assert_eq!(voice[0].1.last().copied(), Some(0.8));

        let fade = jb.playout_round();
        assert_eq!(fade.len(), 1);
        assert_eq!(fade[0].1[0], 0.8);
        assert_eq!(fade[0].1[JITTER_TAIL_FADE_SAMPLES - 1], 0.0);
        assert!(fade[0].1[JITTER_TAIL_FADE_SAMPLES..]
            .iter()
            .all(|sample| *sample == 0.0));
        assert!(
            fade[0]
                .1
                .windows(2)
                .map(|pair| (pair[1] - pair[0]).abs())
                .fold(0.0f32, f32::max)
                < 0.03,
            "fade to silence introduced a click"
        );

        jb.push("a", vec![-0.8; FRAME_SIZE]);
        let resumed = jb.playout_round();
        assert_eq!(resumed[0].1[0], 0.0);
        assert!(
            resumed[0]
                .1
                .windows(2)
                .map(|pair| (pair[1] - pair[0]).abs())
                .fold(0.0f32, f32::max)
                < 0.03,
            "resume from silence introduced a click"
        );
    }

    #[test]
    fn recovered_batch_without_future_packets_fades_and_pads_a_fixed_tail() {
        let mut jb = PeerJitter::with_staging_limits(1, 8);
        jb.push_batch("a", vec![vec![1.0; FRAME_SIZE]; 6], 5);

        let mut emitted = Vec::new();
        for _ in 0..8 {
            let round = jb.playout_round();
            if round.is_empty() {
                break;
            }
            assert_eq!(round.len(), 1);
            assert_eq!(round[0].1.len(), FRAME_SIZE);
            assert!(round[0].1.iter().all(|sample| sample.is_finite()));
            emitted.push(round[0].1.clone());
        }

        assert_eq!(emitted.len(), 6);
        assert!(jb.is_idle());
        assert_eq!(jb.recovery_debt_samples_for("a"), Some(0));
        let tail = emitted.last().unwrap();
        assert_eq!(tail[0], 1.0);
        assert_eq!(tail[FRAME_SIZE / 2 - 1], 0.0);
        assert!(tail[FRAME_SIZE / 2..].iter().all(|sample| *sample == 0.0));
    }

    #[test]
    fn recovered_debt_survives_the_batch_talkspurt_reset() {
        let mut jb = PeerJitter::with_staging_limits(1, 8);
        let start = Instant::now();
        jb.push_at("a", vec![0.0; FRAME_SIZE], start);
        assert_eq!(jb.playout_round().len(), 1);

        jb.push_batch_at(
            "a",
            vec![vec![0.1; FRAME_SIZE], vec![0.2; FRAME_SIZE]],
            1,
            start + JITTER_TALKSPURT_RESET,
        );

        assert_eq!(jb.recovery_debt_samples_for("a"), Some(FRAME_SIZE));
        let round = jb.playout_round();
        assert_eq!(round.len(), 1);
        assert_eq!(round[0].1.len(), FRAME_SIZE);
        assert_eq!(
            jb.recovery_debt_samples_for("a"),
            Some(FRAME_SIZE - FRAME_SIZE / JITTER_CATCHUP_MAX_RATE_DIVISOR)
        );
    }

    #[test]
    fn catchup_overlap_is_bounded_and_keeps_fixed_frame_size() {
        let frame = (0..FRAME_SIZE + FRAME_SIZE / JITTER_CATCHUP_MAX_RATE_DIVISOR)
            .map(|sample| (sample as f32 * 0.031).sin())
            .collect::<Vec<_>>();
        let extra = FRAME_SIZE / JITTER_CATCHUP_MAX_RATE_DIVISOR;
        let output = overlap_accelerate(&frame, FRAME_SIZE, extra);
        assert_eq!(output.len(), FRAME_SIZE);
        assert!(output.iter().all(|sample| sample.is_finite()));
        assert!(
            output
                .windows(2)
                .map(|pair| (pair[1] - pair[0]).abs())
                .fold(0.0f32, f32::max)
                < 0.2,
            "overlap-add catch-up introduced an abrupt discontinuity"
        );
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
    fn jitter_adapts_per_peer_to_sustained_arrival_variance() {
        let mut jb = PeerJitter::with_limits(2, 12);
        let start = Instant::now();
        jb.push_at("steady", vec![0.0], start);
        jb.push_at("jittery", vec![0.0], start);

        let mut steady_at = start;
        let mut jittery_at = start;
        for i in 0..24 {
            steady_at += Duration::from_millis(20);
            jb.push_at("steady", vec![0.0], steady_at);

            // Alternating 10/50 ms delivery preserves the average rate but has enough sustained
            // variance to need more than the healthy peer's 40 ms starting cushion.
            jittery_at += if i % 2 == 0 {
                Duration::from_millis(10)
            } else {
                Duration::from_millis(50)
            };
            jb.push_at("jittery", vec![0.0], jittery_at);
        }

        assert_eq!(jb.target_for("steady"), Some(2));
        assert!(jb.target_for("jittery").unwrap() > 2);
    }

    #[test]
    fn jitter_ignores_decoder_batches_and_long_talkspurt_gaps() {
        let mut jb = PeerJitter::with_limits(2, 12);
        let start = Instant::now();
        jb.push_at("a", vec![0.0], start);
        jb.push_at("a", vec![0.0], start + Duration::from_micros(100));
        jb.push_at("a", vec![0.0], start + Duration::from_secs(2));
        assert_eq!(jb.target_for("a"), Some(2));
    }

    #[test]
    fn new_talkspurt_reprimes_instead_of_bypassing_the_target() {
        let mut jb = PeerJitter::with_limits(2, 12);
        let start = Instant::now();
        jb.push_at("a", vec![1.0], start);
        jb.push_at("a", vec![1.0], start + Duration::from_millis(20));
        assert_eq!(jb.playout_round().len(), 1);
        assert_eq!(jb.playout_round().len(), 1);
        assert_eq!(jb.primed_for("a"), Some(true));

        jb.push_at("a", vec![2.0], start + Duration::from_secs(1));
        assert_eq!(jb.primed_for("a"), Some(false));
        assert!(
            jb.playout_round().is_empty(),
            "one new frame must not bypass reprime"
        );
        jb.push_at("a", vec![2.0], start + Duration::from_millis(1020));
        assert_eq!(jb.playout_round().len(), 1);
    }

    #[test]
    fn adaptive_growth_holds_only_quiet_frames_and_keeps_other_peers_playing() {
        let mut jb = PeerJitter::with_limits(2, 12);
        let start = Instant::now();
        for peer in ["quiet", "voice"] {
            jb.push_at(peer, vec![0.0], start);
            jb.push_at(peer, vec![0.0], start + Duration::from_millis(20));
        }
        assert_eq!(jb.playout_round().len(), 2);

        let quiet = jb.peers.get_mut("quiet").unwrap();
        quiet.target = 3;
        quiet.frames.push_back(vec![0.0]);
        let voice = jb.peers.get_mut("voice").unwrap();
        voice.target = 3;
        voice.frames.clear();
        voice.frames.push_back(vec![0.25]);

        let round = jb.playout_round();
        assert_eq!(
            round.len(),
            1,
            "quiet peer holds while the voiced peer keeps playing"
        );
        assert_eq!(round[0].0, "voice");
        assert_eq!(jb.peers["quiet"].frames.len(), 2);
        assert!(jb.peers["voice"].frames.is_empty());
    }

    #[test]
    fn jitter_depth_decays_after_a_sustained_stable_route() {
        let mut jb = PeerJitter::with_limits(2, 12);
        let start = Instant::now();
        jb.push_at("a", vec![0.0], start);
        let mut at = start;
        for i in 0..24 {
            at += if i % 2 == 0 {
                Duration::from_millis(10)
            } else {
                Duration::from_millis(50)
            };
            jb.push_at("a", vec![0.0], at);
        }
        let raised = jb.target_for("a").unwrap();
        assert!(raised > 2);

        for _ in 0..800 {
            at += Duration::from_millis(20);
            jb.push_at("a", vec![0.0], at);
        }
        assert!(jb.target_for("a").unwrap() < raised);
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
    fn filter_returns_send_signal_without_dry_levels() {
        let lp = Biquad::lowpass(650.0, 0.7);
        let hp = Biquad::highpass(650.0, 0.9);

        // None and Ghost pass the raw sample through; the GhostDry level is applied at the mix
        // stage, not here.
        let (mut z1, mut z2) = (0.0f32, 0.0f32);
        approx(
            apply_filter(FilterMode::None, &lp, &hp, &mut z1, &mut z2, 0.42),
            0.42,
        );
        let (mut gz1, mut gz2) = (0.0f32, 0.0f32);
        approx(
            apply_filter(FilterMode::Ghost, &lp, &hp, &mut gz1, &mut gz2, 0.42),
            0.42,
        );

        // Wall is the 650Hz low-pass with no level trim (WallDry is applied at the mix stage):
        // steady DC settles to the low-pass DC gain of 1.0.
        let (mut wz1, mut wz2) = (0.0f32, 0.0f32);
        let mut wall = 0.0;
        for _ in 0..4000 {
            wall = apply_filter(FilterMode::WallMuffle, &lp, &hp, &mut wz1, &mut wz2, 1.0);
        }
        approx_tol(wall, 1.0, 1e-2);
    }

    #[test]
    fn ghost_and_wall_reverb_stays_bounded_and_decays() {
        let gs = gs_with(
            LocalState { deafened: false },
            1.0,
            vec![
                (
                    "g",
                    PeerState {
                        gain: 1.0,
                        pan: 0.0,
                        mode: 1,
                    },
                ),
                (
                    "w",
                    PeerState {
                        gain: 1.0,
                        pan: 0.0,
                        mode: 3,
                    },
                ),
            ],
        );
        let mut mixer = Mixer::new();
        let g = vec![0.2f32; 960];
        let w = vec![0.2f32; 960];
        let per_peer = vec![
            ("g".to_string(), g.as_slice()),
            ("w".to_string(), w.as_slice()),
        ];
        let mut out = vec![0.0f32; 1920];
        for _ in 0..10 {
            mixer.mix(&per_peer, &gs, &mut out);
            assert!(out
                .iter()
                .all(|s| s.is_finite() && (-1.0..=1.0).contains(s)));
        }
        assert!(
            out.iter().any(|&s| s.abs() > 0.0),
            "ghost/wall produced no output"
        );
        assert!(
            mixer.needs_idle_mix(),
            "effect tails require idle mix rounds"
        );

        // Go silent: the reverb tail must ring out, stay finite, and decay (never run away).
        let empty: Vec<(String, &[f32])> = vec![];
        let mut peak = 0.0f32;
        for _ in 0..60 {
            mixer.mix(&empty, &gs, &mut out);
            peak = out.iter().fold(0.0f32, |m, &s| {
                assert!(s.is_finite());
                m.max(s.abs())
            });
        }
        assert!(peak < 0.2, "reverb tail did not decay: {peak}");
        for _ in 0..40 {
            mixer.mix(&empty, &gs, &mut out);
        }
        assert!(
            !mixer.needs_idle_mix(),
            "effect scheduler must quiesce after the tail"
        );
    }

    #[test]
    fn filter_mode_switch_crossfades_the_per_speaker_boundary() {
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
        let mono = vec![0.2f32; FRAME_SIZE];
        let per_peer = vec![("p".to_string(), mono.as_slice())];
        let mut before = vec![0.0f32; FRAME_SIZE * 2];
        for _ in 0..10 {
            mixer.mix(&per_peer, &gs, &mut before);
        }

        gs.upsert_peer(
            "p".to_string(),
            PeerState {
                gain: 1.0,
                pan: 0.0,
                mode: 3,
            },
        );
        let mut after = vec![0.0f32; FRAME_SIZE * 2];
        mixer.mix(&per_peer, &gs, &mut after);
        assert!(
            (after[0] - before[before.len() - 2]).abs() < 0.01,
            "mode switch introduced an abrupt left-channel boundary"
        );
        assert!(
            (after[1] - before[before.len() - 1]).abs() < 0.01,
            "mode switch introduced an abrupt right-channel boundary"
        );
    }
    #[test]
    fn deafen_transition_discards_reverb_tails_and_filter_history() {
        let gs = gs_with(
            LocalState { deafened: false },
            1.0,
            vec![
                (
                    "ghost",
                    PeerState {
                        gain: 1.0,
                        pan: 0.0,
                        mode: 1,
                    },
                ),
                (
                    "wall",
                    PeerState {
                        gain: 1.0,
                        pan: 0.0,
                        mode: 3,
                    },
                ),
            ],
        );
        let mut mixer = Mixer::new();
        let ghost = vec![0.2f32; 960];
        let wall = vec![0.2f32; 960];
        let per_peer = vec![
            ("ghost".to_string(), ghost.as_slice()),
            ("wall".to_string(), wall.as_slice()),
        ];
        let mut output = vec![0.0f32; 1920];

        for _ in 0..10 {
            mixer.mix(&per_peer, &gs, &mut output);
        }
        assert!(mixer.ghost_tail > 0);
        assert!(mixer.wall_tail > 0);
        assert!(!mixer.glide.is_empty());
        assert!(mixer
            .ghost_reverb
            .comb_l
            .iter()
            .flatten()
            .any(|sample| *sample != 0.0));
        assert!(mixer
            .wall_reverb
            .comb_l
            .iter()
            .flatten()
            .any(|sample| *sample != 0.0));

        // No mixer callback occurs while deafened, which is possible when the decoded jitter
        // queues are idle. The epoch still lets the next callback observe that deafen happened.
        gs.set_local(LocalState { deafened: true });
        gs.set_local(LocalState { deafened: false });
        let empty: Vec<(String, &[f32])> = Vec::new();
        output.fill(1.0);
        mixer.mix(&empty, &gs, &mut output);
        assert!(output.iter().all(|sample| *sample == 0.0));
        assert_eq!(mixer.ghost_tail, 0);
        assert_eq!(mixer.wall_tail, 0);
        assert_eq!(mixer.ghost_lp_z1, 0.0);
        assert_eq!(mixer.ghost_lp_z2, 0.0);
        assert!(mixer.glide.is_empty());
        for reverb in [&mixer.ghost_reverb, &mixer.wall_reverb] {
            assert!(reverb.comb_l.iter().flatten().all(|sample| *sample == 0.0));
            assert!(reverb.comb_r.iter().flatten().all(|sample| *sample == 0.0));
            assert!(reverb.ap_l.iter().flatten().all(|sample| *sample == 0.0));
            assert!(reverb.ap_r.iter().flatten().all(|sample| *sample == 0.0));
            assert!(reverb.filt_l.iter().all(|sample| *sample == 0.0));
            assert!(reverb.filt_r.iter().all(|sample| *sample == 0.0));
        }

        output.fill(1.0);
        mixer.mix(&empty, &gs, &mut output);
        assert!(
            output.iter().all(|sample| *sample == 0.0),
            "pre-deafen effect history must not resume after undeafen"
        );
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

    fn mix_first_sample(master: f32, peer_gain: f32, sample: f32) -> f32 {
        let gs = gs_with(
            LocalState { deafened: false },
            master,
            vec![(
                "p",
                PeerState {
                    gain: peer_gain,
                    pan: 0.0,
                    mode: 0,
                },
            )],
        );
        let mut mixer = Mixer::new();
        let mono = vec![sample; 960];
        let per_peer = vec![("p".to_string(), mono.as_slice())];
        let mut out = vec![0.0; 1920];
        mixer.mix(&per_peer, &gs, &mut out);
        out[0]
    }

    fn mix_left_rms(master: f32, peer_gain: f32, peak: f32) -> f32 {
        let gs = gs_with(
            LocalState { deafened: false },
            master,
            vec![(
                "p",
                PeerState {
                    gain: peer_gain,
                    pan: 0.0,
                    mode: 0,
                },
            )],
        );
        let mut mixer = Mixer::new();
        let mono = (0..960)
            .map(|sample| {
                let phase = std::f32::consts::TAU * 1_000.0 * sample as f32
                    / crate::codec::SAMPLE_RATE as f32;
                peak * phase.sin()
            })
            .collect::<Vec<_>>();
        let per_peer = vec![("p".to_string(), mono.as_slice())];
        let mut out = vec![0.0; 1920];
        mixer.mix(&per_peer, &gs, &mut out);
        (out.iter()
            .step_by(2)
            .map(|sample| sample * sample)
            .sum::<f32>()
            / 960.0)
            .sqrt()
    }

    fn mic_gain_through_codec_and_mixer_rms(input_gain: f32) -> f32 {
        let input = InputConfig::sanitized(input_gain, 0.004);
        let mut codec = OpusCodec::new().expect("opus codec init");
        let state = gs_with(
            LocalState { deafened: false },
            1.0,
            vec![(
                "mic",
                PeerState {
                    gain: 1.0,
                    pan: 0.0,
                    mode: 0,
                },
            )],
        );
        let mut mixer = Mixer::new();
        let mut energy = 0.0;
        let mut samples_measured = 0usize;

        for frame in 0..8 {
            let mut captured = [0.0; FRAME_SIZE];
            for (sample_index, sample) in captured.iter_mut().enumerate() {
                let index = frame * FRAME_SIZE + sample_index;
                let carrier =
                    (std::f32::consts::TAU * 190.0 * index as f32 / SAMPLE_RATE as f32).sin();
                let envelope_phase =
                    std::f32::consts::TAU * 7.0 * index as f32 / SAMPLE_RATE as f32;
                let envelope = 0.12 + 0.88 * (0.5 + 0.5 * envelope_phase.sin()).powi(4);
                *sample = 0.7 * envelope * carrier;
            }
            input.apply_gain(&mut captured);

            let packet = codec.encode(&captured);
            assert!(!packet.is_empty());
            let mut decoded = [0.0; FRAME_SIZE];
            assert_eq!(codec.decode(&packet, &mut decoded), FRAME_SIZE);
            if frame < 2 {
                continue;
            }

            let per_peer = vec![("mic".to_string(), decoded.as_slice())];
            let mut output = [0.0; FRAME_SIZE * 2];
            mixer.mix(&per_peer, &state, &mut output);
            for sample in output.iter().step_by(2) {
                energy += sample * sample;
                samples_measured += 1;
            }
        }

        (energy / samples_measured as f32).sqrt()
    }

    #[test]
    fn master_boost_remains_audibly_monotonic_above_soft_limit_knee() {
        let normal = mix_first_sample(1.0, 1.0, PLAYBACK_SOFT_LIMIT_START);
        let boosted = mix_first_sample(2.0, 1.0, PLAYBACK_SOFT_LIMIT_START);
        approx(normal, PLAYBACK_SOFT_LIMIT_START);
        assert!(boosted > normal, "200% master must be louder than 100%");
        assert!(boosted <= 1.0);
    }

    #[test]
    fn per_peer_boost_remains_audibly_monotonic_above_soft_limit_knee() {
        let normal = mix_first_sample(1.0, 1.0, PLAYBACK_SOFT_LIMIT_START);
        let boosted = mix_first_sample(1.0, 2.0, PLAYBACK_SOFT_LIMIT_START);
        assert!(
            boosted > normal,
            "200% per-player gain must be louder than 100%"
        );
        assert!(boosted <= 1.0);
    }

    #[test]
    fn every_master_and_peer_slider_step_increases_hot_signal_rms() {
        let steps = [1.0, 1.25, 1.5, 1.75, 2.0];
        let mut previous_master = 0.0;
        let mut previous_peer = 0.0;
        for gain in steps {
            let master_rms = mix_left_rms(gain, 1.0, 0.8);
            let peer_rms = mix_left_rms(1.0, gain, 0.8);
            assert!(
                master_rms > previous_master,
                "master step {gain} must raise RMS"
            );
            assert!(peer_rms > previous_peer, "peer step {gain} must raise RMS");
            previous_master = master_rms;
            previous_peer = peer_rms;
        }
    }

    #[test]
    fn every_mic_slider_step_survives_opus_and_receiver_mix() {
        let mut previous = 0.0;
        for gain in [1.0, 1.25, 1.5, 1.75, 2.0] {
            let rms = mic_gain_through_codec_and_mixer_rms(gain);
            assert!(
                rms > previous,
                "mic step {gain} must raise decoded/mixed RMS: previous={previous}, current={rms}"
            );
            previous = rms;
        }
    }

    #[test]
    fn linked_soft_limiter_preserves_boosted_stereo_pan_ratio() {
        let (left, right) = soft_limit_stereo_pair(0.5, 2.0);
        assert!(right > PLAYBACK_SOFT_LIMIT_START);
        approx(right / left, 4.0);
    }

    #[test]
    fn maximum_composed_gain_stays_finite_and_bounded() {
        let output = mix_first_sample(2.0, 4.0, 0.8);
        assert!(output.is_finite());
        assert!((-1.0..=1.0).contains(&output));
    }

    #[test]
    fn soft_limiter_is_finite_bounded_and_preserves_quiet_samples() {
        approx(soft_limit_sample(0.5), 0.5);
        approx(soft_limit_sample(-0.5), -0.5);
        assert_eq!(soft_limit_sample(f32::NAN), 0.0);
        assert!((-1.0..=1.0).contains(&soft_limit_sample(100.0)));
        assert!((-1.0..=1.0).contains(&soft_limit_sample(-100.0)));
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
