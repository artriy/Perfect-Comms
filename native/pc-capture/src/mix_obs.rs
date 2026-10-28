use super::*;
use crate::gamestate::{GameSnapshot, RecordingIdentity};
use crate::obs_recording::{RecordingClock, Shared, MAX_STEMS, SAMPLES};
use std::sync::atomic::Ordering;
use std::sync::Arc;

struct Stem {
    peer_id: String,
    peer_epoch: u64,
    assigned: bool,
    ghost_dirty: bool,
    wall_dirty: bool,
    buffers_dirty: bool,
    identity: RecordingIdentity,
    glide: Glide,
    ghost: Reverb,
    wall: Reverb,
    ghost_z1: f32,
    ghost_z2: f32,
    ghost_tail: usize,
    wall_tail: usize,
    ghost_input: bool,
    wall_input: bool,
    seen: bool,
    out: [f32; SAMPLES],
    ghost_send: [f32; SAMPLES],
    wall_send: [f32; SAMPLES],
    delay: [f32; crate::loudness::LIMITER_LOOKAHEAD_FRAMES * 2],
    delay_index: usize,
    pending_delay: bool,
}

impl Stem {
    fn new(identity: RecordingIdentity, peer: &crate::gamestate::PeerState) -> Self {
        let (left, right) = pan_gains(peer.pan);
        let mode = FilterMode::from_i32(peer.mode);
        Self {
            peer_id: String::with_capacity(192),
            peer_epoch: 0,
            assigned: false,
            ghost_dirty: false,
            wall_dirty: false,
            buffers_dirty: false,
            identity,
            glide: Glide {
                left: left * peer.gain,
                right: right * peer.gain,
                mode,
                bz1: 0.0,
                bz2: 0.0,
                previous_mode: mode,
                previous_bz1: 0.0,
                previous_bz2: 0.0,
                transition_remaining: 0,
                muffled: peer.muffled,
                previous_muffled: peer.muffled,
                muffle_path: MufflePath::default(),
                previous_primary_muffle_path: MufflePath::default(),
                muffle_transition_remaining: 0,
                seen_round: true,
            },
            ghost: Reverb::new(&GHOST_COMBS, &GHOST_ALLPASS, 0.82, 25),
            wall: Reverb::new(&WALL_COMBS, &WALL_ALLPASS, 0.6, 11),
            ghost_z1: 0.0,
            ghost_z2: 0.0,
            ghost_tail: 0,
            wall_tail: 0,
            ghost_input: false,
            wall_input: false,
            seen: false,
            out: [0.0; SAMPLES],
            ghost_send: [0.0; SAMPLES],
            wall_send: [0.0; SAMPLES],
            delay: [0.0; crate::loudness::LIMITER_LOOKAHEAD_FRAMES * 2],
            delay_index: 0,
            pending_delay: false,
        }
    }

    fn assign(
        &mut self,
        peer_id: &str,
        peer_epoch: u64,
        identity: Option<&RecordingIdentity>,
        peer: &crate::gamestate::PeerState,
    ) {
        self.peer_id.clear();
        self.peer_id.push_str(peer_id);
        self.peer_epoch = peer_epoch;
        self.assigned = true;
        self.identity.name.clear();
        match identity.filter(|identity| identity.id != 0 && identity.group != 0) {
            Some(identity) => {
                self.identity.id = identity.id;
                self.identity.group = identity.group;
                self.identity.name.push_str(&identity.name);
            }
            None => {
                self.identity.id = 0;
                self.identity.group = 0;
                self.identity.name.push_str("Private voices");
            }
        }
        let (left, right) = pan_gains(peer.pan);
        let mode = FilterMode::from_i32(peer.mode);
        self.glide = Glide {
            left: left * peer.gain,
            right: right * peer.gain,
            mode,
            bz1: 0.0,
            bz2: 0.0,
            previous_mode: mode,
            previous_bz1: 0.0,
            previous_bz2: 0.0,
            transition_remaining: 0,
            muffled: peer.muffled,
            previous_muffled: peer.muffled,
            muffle_path: MufflePath::default(),
            previous_primary_muffle_path: MufflePath::default(),
            muffle_transition_remaining: 0,
            seen_round: true,
        };
    }

    fn clear_round(&mut self) {
        if self.buffers_dirty {
            self.out.fill(0.0);
            self.ghost_send.fill(0.0);
            self.wall_send.fill(0.0);
            self.buffers_dirty = false;
        }
        self.seen = false;
        self.ghost_input = false;
        self.wall_input = false;
    }

    fn release(&mut self) {
        if !self.assigned {
            return;
        }
        if self.ghost_dirty {
            self.ghost.reset();
            self.ghost_dirty = false;
        }
        if self.wall_dirty {
            self.wall.reset();
            self.wall_dirty = false;
        }
        self.ghost_z1 = 0.0;
        self.ghost_z2 = 0.0;
        self.ghost_tail = 0;
        self.wall_tail = 0;
        self.delay.fill(0.0);
        self.delay_index = 0;
        self.pending_delay = false;
        self.clear_round();
        self.assigned = false;
        self.peer_id.clear();
        self.identity.name.clear();
    }

    fn finish(&mut self, master: f32, lowpass: Biquad) -> bool {
        let active = self.seen || self.ghost_tail > 0 || self.wall_tail > 0 || self.pending_delay;
        if !active {
            return false;
        }
        self.buffers_dirty = true;
        self.pending_delay = self.seen || self.ghost_tail > 0 || self.wall_tail > 0;
        let frames = SAMPLES / 2;
        if self.ghost_input {
            self.ghost_tail = GHOST_TAIL_SAMPLES;
            self.ghost_dirty = true;
        }
        if self.wall_input {
            self.wall_tail = WALL_TAIL_SAMPLES;
            self.wall_dirty = true;
        }
        for frame in 0..frames {
            let left = frame * 2;
            let right = left + 1;
            if self.ghost_tail > 0 {
                let mono = lowpass.process(
                    &mut self.ghost_z1,
                    &mut self.ghost_z2,
                    (self.ghost_send[left] + self.ghost_send[right]) * 0.5,
                );
                let (wl, wr) = self.ghost.process(mono);
                self.out[left] += GHOST_DRY * self.ghost_send[left] + GHOST_WET * wl;
                self.out[right] += GHOST_DRY * self.ghost_send[right] + GHOST_WET * wr;
            }
            if self.wall_tail > 0 {
                let (wl, wr) = self
                    .wall
                    .process((self.wall_send[left] + self.wall_send[right]) * 0.5);
                self.out[left] += WALL_DRY * self.wall_send[left] + WALL_WET * wl;
                self.out[right] += WALL_DRY * self.wall_send[right] + WALL_WET * wr;
            }
            self.out[left] *= master;
            self.out[right] *= master;
            for index in [left, right] {
                std::mem::swap(&mut self.out[index], &mut self.delay[self.delay_index]);
                self.delay_index = (self.delay_index + 1) % self.delay.len();
            }
        }
        if !self.ghost_input {
            self.ghost_tail = self.ghost_tail.saturating_sub(frames);
            if self.ghost_tail == 0 && self.ghost_dirty {
                self.ghost.reset();
                self.ghost_dirty = false;
                self.ghost_z1 = 0.0;
                self.ghost_z2 = 0.0;
            }
        }
        if !self.wall_input {
            self.wall_tail = self.wall_tail.saturating_sub(frames);
            if self.wall_tail == 0 && self.wall_dirty {
                self.wall.reset();
                self.wall_dirty = false;
            }
        }
        if !self.seen {
            self.glide.bz1 = 0.0;
            self.glide.bz2 = 0.0;
            self.glide.previous_bz1 = 0.0;
            self.glide.previous_bz2 = 0.0;
            self.glide.transition_remaining = 0;
        }
        active
    }
}

pub(crate) struct RecordingPool {
    stems: Vec<Stem>,
}

impl RecordingPool {
    pub(crate) fn new() -> Self {
        let peer = crate::gamestate::PeerState {
            gain: 0.0,
            pan: 0.0,
            mode: 0,
            muffled: false,
        };
        let mut stems = Vec::with_capacity(MAX_STEMS);
        for _ in 0..MAX_STEMS {
            stems.push(Stem::new(
                RecordingIdentity {
                    id: 0,
                    name: String::with_capacity(192),
                    group: 0,
                },
                &peer,
            ));
        }
        Self { stems }
    }
}

pub(super) struct RecordingMix {
    pub bridge: Arc<Shared>,
    stems: Vec<Stem>,
    reset_epoch: u64,
    clock: RecordingClock,
    connection: u64,
    active: bool,
    route_epoch: u64,
    filters: (Biquad, Biquad, Biquad),
}

impl RecordingMix {
    pub fn new(bridge: Arc<Shared>, filters: (Biquad, Biquad, Biquad)) -> Self {
        Self {
            bridge,
            stems: Vec::new(),
            reset_epoch: 0,
            clock: RecordingClock::default(),
            connection: 0,
            active: false,
            route_epoch: 0,
            filters,
        }
    }

    pub fn remove(&mut self, peer: &str) {
        if let Some(stem) = self
            .stems
            .iter_mut()
            .find(|stem| stem.assigned && stem.peer_id == peer)
        {
            stem.release();
        }
    }

    pub fn begin(&mut self, snapshot: &GameSnapshot, active: bool) {
        let connection = self.bridge.connected.load(Ordering::Acquire);
        let route_epoch = self.bridge.route_epoch.load(Ordering::Acquire);
        self.active = active && !snapshot.local.deafened && snapshot.master > 0.0;
        if self.active && self.stems.is_empty() {
            if let Some(pool) = self.bridge.take_recording_pool() {
                self.stems = pool.stems;
            }
        }
        let reset = !self.active
            || self.reset_epoch != snapshot.recording_reset_epoch
            || self.connection != connection
            || self.route_epoch != route_epoch;
        for stem in &mut self.stems {
            if !stem.assigned {
                continue;
            }
            if reset
                || snapshot
                    .recording_peer_epochs
                    .get(&stem.peer_id)
                    .copied()
                    .unwrap_or(0)
                    != stem.peer_epoch
                || !snapshot
                    .peers
                    .get(&stem.peer_id)
                    .is_some_and(|peer| peer.gain > 0.0)
            {
                stem.release();
            } else {
                stem.clear_round();
            }
        }
        if connection == 0 {
            self.clock.reset();
        }
        self.reset_epoch = snapshot.recording_reset_epoch;
        self.connection = connection;
        self.route_epoch = route_epoch;
    }

    pub fn render(
        &mut self,
        peer_id: &str,
        mono: &[f32],
        ramp: crate::loudness::GainRamp,
        snapshot: &GameSnapshot,
    ) {
        if !self.active {
            return;
        }
        let Some(peer) = snapshot.peers.get(peer_id).filter(|peer| peer.gain > 0.0) else {
            self.remove(peer_id);
            return;
        };
        let slot = self
            .stems
            .iter()
            .position(|stem| stem.assigned && stem.peer_id == peer_id)
            .or_else(|| self.stems.iter().position(|stem| !stem.assigned));
        let Some(slot) = slot else {
            self.bridge.failed.store(true, Ordering::Release);
            return;
        };
        let stem = &mut self.stems[slot];
        if !stem.assigned {
            stem.assign(
                peer_id,
                snapshot
                    .recording_peer_epochs
                    .get(peer_id)
                    .copied()
                    .unwrap_or(0),
                snapshot.recording.get(peer_id),
                peer,
            );
        }
        stem.buffers_dirty = true;
        stem.seen = true;
        let (ghost, wall) = render_peer(
            &mut stem.glide,
            mono,
            ramp,
            peer,
            self.filters,
            (&mut stem.out, &mut stem.ghost_send, &mut stem.wall_send),
        );
        stem.ghost_input |= ghost;
        stem.wall_input |= wall;
    }

    pub fn publish(
        &mut self,
        gs: &GameState,
        combined: &[f32],
        audible: bool,
        timestamp: u64,
        lowpass: Biquad,
    ) {
        if !self.bridge.active() {
            return;
        }
        if self.connection != self.bridge.connected.load(Ordering::Acquire) {
            return;
        }
        let Some(timestamp) = self.clock.next(self.connection, timestamp) else {
            return;
        };
        let snapshot = gs.snapshot();
        let authorized = audible
            && !snapshot.local.deafened
            && snapshot.master > 0.0
            && snapshot.recording_reset_epoch == self.reset_epoch
            && self.route_epoch == self.bridge.route_epoch.load(Ordering::Acquire);
        self.bridge.set_audible(authorized);
        if !authorized {
            for stem in &mut self.stems {
                stem.release();
            }
            self.bridge.publish(
                &snapshot,
                timestamp,
                None,
                &[0.0; SAMPLES],
                self.connection,
                self.route_epoch,
            );
            return;
        }
        self.bridge.publish(
            &snapshot,
            timestamp,
            None,
            combined,
            self.connection,
            self.route_epoch,
        );
        for stem in &mut self.stems {
            if !stem.assigned {
                continue;
            }
            if snapshot.recording_peer_epochs.get(&stem.peer_id) != Some(&stem.peer_epoch)
                || !snapshot
                    .peers
                    .get(&stem.peer_id)
                    .is_some_and(|peer| peer.gain > 0.0)
            {
                stem.release();
                continue;
            }
            if stem.finish(snapshot.master, lowpass) {
                self.bridge.publish(
                    &snapshot,
                    timestamp,
                    Some((&stem.identity, stem.peer_epoch)),
                    &stem.out,
                    self.connection,
                    self.route_epoch,
                );
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::gamestate::{LocalState, PeerState};
    pub(super) fn filters() -> (Biquad, Biquad, Biquad) {
        (
            Biquad::lowpass(650.0, 0.7),
            Biquad::highpass(650.0, 0.9),
            Biquad::lowpass(MUFFLE_CUTOFF_HZ, 0.7),
        )
    }
    pub(super) fn state(id: u32) -> GameState {
        let state = GameState::new();
        state.apply_recording(
            LocalState::default(),
            1.0,
            vec![(
                "network-secret".into(),
                PeerState {
                    gain: 1.0,
                    pan: 0.0,
                    mode: 1,
                    muffled: false,
                },
            )],
            HashMap::from([(
                "network-secret".into(),
                RecordingIdentity {
                    id,
                    name: "Public alias".into(),
                    group: 1,
                },
            )]),
        );
        state
    }
    #[test]
    fn identity_and_deafen_changes_discard_recording_tails() {
        let bridge = crate::obs_recording::ObsRecording::new();
        let (_, _pending) = bridge.shared.test_connect();
        let mut tap = RecordingMix::new(bridge.shared.clone(), filters());
        let gs = state(4);
        tap.begin(&gs.snapshot(), true);
        tap.render(
            "network-secret",
            &[0.1; 960],
            crate::loudness::GainRamp {
                start: 1.0,
                end: 1.0,
            },
            &gs.snapshot(),
        );
        assert_eq!(tap.stems.iter().filter(|stem| stem.assigned).count(), 1);
        let replacement = state(5).snapshot();
        gs.apply_recording(
            replacement.local.clone(),
            1.0,
            replacement
                .peers
                .iter()
                .map(|(id, p)| (id.clone(), p.clone()))
                .collect(),
            replacement.recording.clone(),
        );
        tap.begin(&gs.snapshot(), true);
        assert!(tap.stems.iter().all(|stem| !stem.assigned));
        gs.set_local(LocalState { deafened: true });
        tap.begin(&gs.snapshot(), true);
        assert!(!tap.active);
    }
    #[test]
    fn absent_or_private_metadata_never_uses_transport_identity() {
        let bridge = crate::obs_recording::ObsRecording::new();
        let (_, _pending) = bridge.shared.test_connect();
        let mut tap = RecordingMix::new(bridge.shared.clone(), filters());
        let gs = state(0);
        tap.begin(&gs.snapshot(), true);
        tap.render(
            "network-secret",
            &[0.1; 960],
            crate::loudness::GainRamp {
                start: 1.0,
                end: 1.0,
            },
            &gs.snapshot(),
        );
        let stem = tap
            .stems
            .iter()
            .find(|stem| stem.assigned && stem.peer_id == "network-secret")
            .unwrap();
        assert_eq!(stem.identity.id, 0);
        assert_eq!(stem.identity.name, "Private voices");
        assert_eq!(stem.identity.group, 0);
    }

    #[test]
    fn optional_tap_does_not_change_final_playback() {
        let bridge = crate::obs_recording::ObsRecording::new();
        let (_, _pending) = bridge.shared.test_connect();
        let gs = state(42);
        let mut baseline = Mixer::new();
        let mut tapped = Mixer::new();
        tapped.set_recording_bridge(bridge.shared.clone());
        for mode in 0..4 {
            gs.upsert_peer(
                "network-secret".into(),
                PeerState {
                    gain: 1.3,
                    pan: 0.7,
                    mode,
                    muffled: true,
                },
            );
            for _ in 0..4 {
                let mut expected = [0.0; SAMPLES];
                let mut actual = [0.0; SAMPLES];
                baseline.mix_with_measurement(
                    [("network-secret", &[0.05; 960][..], true)],
                    &gs,
                    &mut expected,
                );
                tapped.mix_with_measurement(
                    [("network-secret", &[0.05; 960][..], true)],
                    &gs,
                    &mut actual,
                );
                assert_eq!(actual, expected);
            }
        }
    }

    #[test]
    fn stem_delay_matches_combined_limiter_without_limiting() {
        let peer = PeerState {
            gain: 1.0,
            pan: 0.0,
            mode: 0,
            muffled: false,
        };
        let mut stem = Stem::new(RecordingIdentity::default(), &peer);
        stem.seen = true;
        stem.out[0] = 0.5;
        assert!(stem.finish(1.0, Biquad::lowpass(1900.0, 0.7)));
        assert!(stem.out[..crate::loudness::LIMITER_LOOKAHEAD_FRAMES * 2]
            .iter()
            .all(|sample| *sample == 0.0));
        assert_eq!(stem.out[crate::loudness::LIMITER_LOOKAHEAD_FRAMES * 2], 0.5);
    }

    #[test]
    fn unavailable_routes_and_mutes_discard_dsp_before_resume() {
        let bridge = crate::obs_recording::ObsRecording::new();
        let (_, _pending) = bridge.shared.test_connect();
        let mut tap = RecordingMix::new(bridge.shared.clone(), filters());
        let gs = state(7);
        tap.begin(&gs.snapshot(), true);
        tap.render(
            "network-secret",
            &[0.1; 960],
            crate::loudness::GainRamp {
                start: 1.0,
                end: 1.0,
            },
            &gs.snapshot(),
        );
        bridge.shared.cancel();
        tap.begin(&gs.snapshot(), true);
        assert!(tap.stems.iter().all(|stem| !stem.assigned));
        gs.upsert_peer(
            "network-secret".into(),
            PeerState {
                gain: 0.0,
                pan: 0.0,
                mode: 1,
                muffled: false,
            },
        );
        tap.begin(&gs.snapshot(), true);
        tap.render(
            "network-secret",
            &[1.0; 960],
            crate::loudness::GainRamp {
                start: 1.0,
                end: 1.0,
            },
            &gs.snapshot(),
        );
        assert!(tap.stems.iter().all(|stem| !stem.assigned));
    }
}

#[cfg(test)]
mod lifecycle_tests {
    use super::tests::{filters, state};
    use super::*;

    #[test]
    fn first_batch_delay_survives_audibility_and_unrelated_peer_epoch_changes() {
        let bridge = crate::obs_recording::ObsRecording::new();
        let (_, pending) = bridge.shared.test_connect();
        let mut tap = RecordingMix::new(bridge.shared.clone(), filters());
        let gs = state(7);
        gs.upsert_peer(
            "network-secret".into(),
            crate::gamestate::PeerState {
                gain: 1.0,
                pan: 0.0,
                mode: 0,
                muffled: false,
            },
        );
        let mut mono = [0.0; 960];
        mono[959] = 0.5;
        tap.begin(&gs.snapshot(), true);
        tap.render(
            "network-secret",
            &mono,
            crate::loudness::GainRamp {
                start: 1.0,
                end: 1.0,
            },
            &gs.snapshot(),
        );
        let route = bridge.shared.route_epoch.load(Ordering::Acquire);
        tap.publish(
            &gs,
            &[0.0; SAMPLES],
            true,
            1_000_000_000,
            Biquad::lowpass(1900.0, 0.7),
        );
        assert_eq!(route, bridge.shared.route_epoch.load(Ordering::Acquire));
        gs.upsert_peer(
            "unrelated".into(),
            crate::gamestate::PeerState {
                gain: 1.0,
                pan: 0.0,
                mode: 0,
                muffled: false,
            },
        );
        tap.begin(&gs.snapshot(), true);
        tap.publish(
            &gs,
            &[0.0; SAMPLES],
            true,
            1_023_000_000,
            Biquad::lowpass(1900.0, 0.7),
        );
        let frames: Vec<_> = pending.try_iter().collect();
        assert_eq!(
            frames
                .iter()
                .filter(|frame| frame.combined)
                .map(|frame| frame.timestamp)
                .collect::<Vec<_>>(),
            [1_000_000_000, 1_020_000_000]
        );
        let last = frames.iter().rev().find(|frame| !frame.combined).unwrap();
        assert!(last.samples.iter().any(|sample| *sample > 0.0));
        for frame in frames {
            bridge.shared.test_recycle(frame);
        }
    }

    #[test]
    fn master_mute_reuses_capacity_without_assigning_or_reconstructing_stems() {
        let bridge = crate::obs_recording::ObsRecording::new();
        let (_, _pending) = bridge.shared.test_connect();
        let mut tap = RecordingMix::new(bridge.shared.clone(), filters());
        let gs = state(7);
        tap.begin(&gs.snapshot(), true);
        let storage = tap.stems.as_ptr();
        let ghost = tap.stems[0].ghost.comb_l[0].as_ptr();
        gs.set_master(0.0);
        for _ in 0..4 {
            tap.begin(&gs.snapshot(), true);
            tap.render(
                "network-secret",
                &[1.0; 960],
                crate::loudness::GainRamp {
                    start: 1.0,
                    end: 1.0,
                },
                &gs.snapshot(),
            );
            assert!(!tap.active);
            assert!(tap.stems.iter().all(|stem| !stem.assigned));
        }
        gs.set_master(1.0);
        tap.begin(&gs.snapshot(), true);
        tap.render(
            "network-secret",
            &[1.0; 960],
            crate::loudness::GainRamp {
                start: 1.0,
                end: 1.0,
            },
            &gs.snapshot(),
        );
        gs.set_local(crate::gamestate::LocalState { deafened: true });
        tap.begin(&gs.snapshot(), true);
        assert_eq!(tap.stems.as_ptr(), storage);
        assert_eq!(tap.stems[0].ghost.comb_l[0].as_ptr(), ghost);
        assert!(tap.stems.iter().all(|stem| !stem.assigned));
    }

    #[test]
    fn reconnect_drops_old_delay_and_uses_new_qpc_anchor() {
        let bridge = crate::obs_recording::ObsRecording::new();
        let (old, pending) = bridge.shared.test_connect();
        let mut tap = RecordingMix::new(bridge.shared.clone(), filters());
        let gs = state(7);
        tap.begin(&gs.snapshot(), true);
        tap.render(
            "network-secret",
            &[0.5; 960],
            crate::loudness::GainRamp {
                start: 1.0,
                end: 1.0,
            },
            &gs.snapshot(),
        );
        bridge.shared.cancel();
        let (new, _) = bridge.shared.test_connect();
        assert_ne!(old, new);
        tap.publish(
            &gs,
            &[0.5; SAMPLES],
            true,
            1_000_000_000,
            Biquad::lowpass(1900.0, 0.7),
        );
        assert!(pending.is_empty());
        tap.begin(&gs.snapshot(), true);
        assert!(tap.stems.iter().all(|stem| !stem.assigned));
        tap.publish(
            &gs,
            &[0.0; SAMPLES],
            true,
            2_000_000_000,
            Biquad::lowpass(1900.0, 0.7),
        );
        let frame = pending.try_recv().unwrap();
        assert_eq!(frame.connection, new);
        assert_eq!(frame.timestamp, 2_000_000_000);
        assert!(frame.combined);
        bridge.shared.test_recycle(frame);
    }
}

#[cfg(test)]
mod peer_epoch_tests {
    use super::tests::filters;
    use super::*;
    use crate::gamestate::{LocalState, PeerState};

    fn apply(gs: &GameState, b_muffled: bool) {
        let peer = |muffled| PeerState {
            gain: 1.0,
            pan: 0.0,
            mode: 0,
            muffled,
        };
        let identity = |id| RecordingIdentity {
            id,
            name: format!("Alias {id}"),
            group: 1,
        };
        gs.apply_recording(
            LocalState::default(),
            1.0,
            vec![("a".into(), peer(false)), ("b".into(), peer(b_muffled))],
            HashMap::from([("a".into(), identity(1)), ("b".into(), identity(2))]),
        );
    }

    fn mixed_tick(
        gs: &GameState,
        change: impl FnOnce(),
    ) -> (
        crate::obs_recording::ObsRecording,
        Vec<Box<crate::obs_recording::AudioFrame>>,
    ) {
        let bridge = crate::obs_recording::ObsRecording::new();
        let (_, pending) = bridge.shared.test_connect();
        let mut tap = RecordingMix::new(bridge.shared.clone(), filters());
        tap.begin(&gs.snapshot(), true);
        for peer in ["a", "b"] {
            tap.render(
                peer,
                &[0.1; 960],
                crate::loudness::GainRamp {
                    start: 1.0,
                    end: 1.0,
                },
                &gs.snapshot(),
            );
        }
        change();
        tap.publish(
            gs,
            &[0.25; SAMPLES],
            true,
            1_000_000_000,
            Biquad::lowpass(1900.0, 0.7),
        );
        let frames = pending.try_iter().collect();
        (bridge, frames)
    }

    #[test]
    fn unrelated_peer_change_keeps_combined_and_other_stems() {
        let gs = GameState::new();
        apply(&gs, false);
        let (bridge, frames) = mixed_tick(&gs, || apply(&gs, true));
        let combined = frames.iter().find(|frame| frame.combined).unwrap();
        assert!(combined.samples.iter().all(|sample| *sample == 0.25));
        let stems: Vec<_> = frames
            .iter()
            .filter(|frame| !frame.combined)
            .map(|frame| frame.identity)
            .collect();
        assert_eq!(stems, [1]);
        for frame in frames {
            bridge.shared.test_recycle(frame);
        }
    }

    #[test]
    fn deafen_before_publish_still_drops_every_stem() {
        let gs = GameState::new();
        apply(&gs, false);
        let (bridge, frames) = mixed_tick(&gs, || gs.set_local(LocalState { deafened: true }));
        assert_eq!(frames.len(), 1);
        assert!(frames[0].combined);
        assert!(frames[0].samples.iter().all(|sample| *sample == 0.0));
        for frame in frames {
            bridge.shared.test_recycle(frame);
        }
    }
}
