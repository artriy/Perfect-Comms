use std::collections::{HashMap, VecDeque};
use std::time::{Duration, Instant};

use crate::gamestate::GameState;

pub const GAIN_GLIDE_K: f32 = 0.002;
pub const PLAYBACK_MIX_PEAK_CEILING: f32 = 0.92;
pub const PLAYBACK_MIX_LIMITER_RELEASE_PER_FRAME: f32 = 0.05;
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
        FilterMode::WallMuffle | FilterMode::ListenerMuffle => lp650.process(z1, z2, s),
        FilterMode::Ghost => s,
        FilterMode::None => s,
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
}

pub struct Mixer {
    limiter_gain: f32,
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
            lp1900: Biquad::lowpass(1900.0, 0.7),
            ghost_reverb: Reverb::new(&GHOST_COMBS, &GHOST_ALLPASS, 0.82, 25),
            wall_reverb: Reverb::new(&WALL_COMBS, &WALL_ALLPASS, 0.6, 11),
            ghost_send: Vec::new(),
            wall_send: Vec::new(),
            ghost_lp_z1: 0.0,
            ghost_lp_z2: 0.0,
            ghost_tail: 0,
            wall_tail: 0,
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
                let sl = s * g.left;
                let sr = s * g.right;
                // Ghost / occlusion routes go to a reverb send bus (mixed back below with their
                // dry+wet levels); everyone else mixes straight to the output.
                match mode {
                    FilterMode::Ghost => {
                        self.ghost_send[2 * f] += sl;
                        self.ghost_send[2 * f + 1] += sr;
                    }
                    FilterMode::WallMuffle | FilterMode::ListenerMuffle => {
                        self.wall_send[2 * f] += sl;
                        self.wall_send[2 * f + 1] += sr;
                    }
                    _ => {
                        out_stereo[2 * f] += sl;
                        out_stereo[2 * f + 1] += sr;
                    }
                }
            }
            match mode {
                FilterMode::Ghost => any_ghost = true,
                FilterMode::WallMuffle | FilterMode::ListenerMuffle => any_wall = true,
                _ => {}
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
const JITTER_FRAME_DURATION: Duration = Duration::from_millis(20);
const JITTER_TALKSPURT_RESET: Duration = Duration::from_millis(250);
const JITTER_UNDERRUN_REBUFFER: Duration = Duration::from_millis(40);
const JITTER_SAME_DRAIN_THRESHOLD: Duration = Duration::from_millis(2);
const JITTER_EWMA_GAIN: f64 = 1.0 / 16.0;
const JITTER_TARGET_GAIN: f64 = 2.5;
const JITTER_STABLE_ARRIVALS_TO_DECAY: usize = 250;
const JITTER_QUIET_PEAK: f32 = 0.003;
const JITTER_QUIET_RMS: f32 = 0.001;

struct PeerQueue {
    frames: VecDeque<Vec<f32>>,
    primed: bool,
    target: usize,
    last_arrival: Option<Instant>,
    jitter_frames: f64,
    stable_arrivals: usize,
    shed_quiet_frame: bool,
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
        self.push_at(peer, frame, Instant::now());
    }

    fn push_at(&mut self, peer: &str, frame: Vec<f32>, now: Instant) {
        let base_prime = self.prime;
        let cap = self.cap;
        let q = self
            .peers
            .entry(peer.to_string())
            .or_insert_with(|| PeerQueue {
                frames: VecDeque::new(),
                primed: false,
                target: base_prime,
                last_arrival: None,
                jitter_frames: 0.0,
                stable_arrivals: 0,
                shed_quiet_frame: false,
            });

        let interval = q
            .last_arrival
            .map(|previous| now.saturating_duration_since(previous));
        let was_empty = q.frames.is_empty();
        Self::observe_arrival(q, now, base_prime, cap);

        // A long transport stall or a measured empty-queue underrun starts a new playout
        // generation. The quiet-frame hold in playout_round grows a live buffer without freezing
        // speech; this reset is the fallback when no quiet audio is available.
        if interval.is_some_and(|elapsed| elapsed >= JITTER_TALKSPURT_RESET) {
            q.frames.clear();
            q.primed = false;
            q.shed_quiet_frame = false;
        } else if q.primed
            && was_empty
            && interval.is_some_and(|elapsed| elapsed >= JITTER_UNDERRUN_REBUFFER)
        {
            q.primed = false;
        }
        if q.frames.len() >= cap {
            q.frames.pop_front();
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

    pub fn playout_round(&mut self) -> Vec<(String, Vec<f32>)> {
        let mut out = Vec::new();
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
            if q.frames.len() < q.target
                && q.frames.front().is_some_and(|frame| is_quiet_frame(frame))
            {
                continue;
            }
            if q.shed_quiet_frame
                && q.frames.len() > q.target
                && q.frames.front().is_some_and(|frame| is_quiet_frame(frame))
            {
                q.frames.pop_front();
                q.shed_quiet_frame = false;
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

    #[cfg(test)]
    fn target_for(&self, peer: &str) -> Option<usize> {
        self.peers.get(peer).map(|q| q.target)
    }

    #[cfg(test)]
    fn primed_for(&self, peer: &str) -> Option<bool> {
        self.peers.get(peer).map(|q| q.primed)
    }
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
