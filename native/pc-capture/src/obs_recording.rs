use crate::gamestate::{GameSnapshot, GameState, RecordingIdentity};
use crossbeam_channel::{bounded, Receiver, Sender};
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicU8, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;

pub(crate) const SAMPLES: usize = 1920;
const POOL_SIZE: usize = 256;
pub(crate) const MAX_STEMS: usize = 64;

#[derive(Debug)]
pub(crate) struct AudioFrame {
    pub timestamp: u64,
    pub identity: u32,
    pub group: u8,
    pub name_len: usize,
    pub name: [u8; 192],
    pub samples: [f32; SAMPLES],
    pub reset_epoch: u64,
    pub peer_epoch: u64,
    pub connection: u64,
    pub route_epoch: u64,
    pub combined: bool,
}

impl Default for AudioFrame {
    fn default() -> Self {
        Self {
            timestamp: 0,
            identity: 0,
            group: 0,
            name_len: 0,
            name: [0; 192],
            samples: [0.0; SAMPLES],
            reset_epoch: 0,
            peer_epoch: 0,
            connection: 0,
            route_epoch: 0,
            combined: false,
        }
    }
}

struct Publisher {
    free: Receiver<Box<AudioFrame>>,
    pending: Sender<Box<AudioFrame>>,
    recycle: Sender<Box<AudioFrame>>,
    queued: Receiver<Box<AudioFrame>>,
}

pub(crate) struct Shared {
    pub connected: AtomicU64,
    pub failed: AtomicBool,
    mode: AtomicU8,
    audible: AtomicBool,
    pub route_epoch: AtomicU64,
    // Channels keep one lifetime; retirement never takes a media-thread lock.
    publisher: Publisher,
    pool_ready: AtomicBool,
    publishing: AtomicUsize,
    next_connection: AtomicU64,
    prepared_dsp: Sender<crate::mix::RecordingPool>,
    available_dsp: Receiver<crate::mix::RecordingPool>,
    status: Mutex<Option<(&'static str, String)>>,
}

impl Shared {
    fn new() -> Self {
        let (recycle, free) = bounded(POOL_SIZE);
        let (pending, queued) = bounded(POOL_SIZE);
        let (prepared_dsp, available_dsp) = bounded(1);
        Self {
            connected: AtomicU64::new(0),
            failed: AtomicBool::new(false),
            mode: AtomicU8::new(0),
            audible: AtomicBool::new(false),
            route_epoch: AtomicU64::new(0),
            publisher: Publisher {
                free,
                pending,
                recycle,
                queued,
            },
            pool_ready: AtomicBool::new(false),
            publishing: AtomicUsize::new(0),
            next_connection: AtomicU64::new(1),
            status: Mutex::new(None),
            prepared_dsp,
            available_dsp,
        }
    }

    pub fn active(&self) -> bool {
        self.connected.load(Ordering::Acquire) != 0 && !self.failed.load(Ordering::Acquire)
    }

    pub fn set_audible(&self, audible: bool) {
        self.audible.store(audible, Ordering::Release);
    }

    pub fn status(&self, state: &'static str, text: impl Into<String>) {
        *self.status.lock().unwrap() = Some((state, text.into()));
    }

    pub fn take_status(&self) -> Option<(&'static str, String)> {
        self.status.lock().unwrap().take()
    }

    fn initialize_pool(&self) -> std::io::Result<()> {
        if !self.pool_ready.swap(true, Ordering::AcqRel) {
            for _ in 0..POOL_SIZE {
                self.publisher
                    .recycle
                    .send(Box::new(AudioFrame::default()))
                    .expect("empty frame pool");
            }
            self.prepared_dsp
                .try_send(crate::mix::RecordingPool::new())
                .map_err(|_| std::io::Error::other("OBS recording DSP pool unavailable"))?;
        }
        Ok(())
    }

    pub(crate) fn take_recording_pool(&self) -> Option<crate::mix::RecordingPool> {
        self.available_dsp.try_recv().ok()
    }

    pub(super) fn begin_connection(&self) -> u64 {
        self.set_audible(false);
        let connection = self.next_connection.fetch_add(1, Ordering::AcqRel).max(1);
        self.connected.store(connection, Ordering::SeqCst);
        connection
    }

    pub fn cancel(&self) {
        self.connected.store(0, Ordering::SeqCst);
        self.route_epoch.fetch_add(1, Ordering::AcqRel);
        self.set_audible(false);
    }

    pub(super) fn frame_authorized(
        &self,
        frame: &AudioFrame,
        snapshot: &GameSnapshot,
        connection: u64,
    ) -> bool {
        connection != 0
            && frame.connection == connection
            && self.connected.load(Ordering::Acquire) == connection
            && frame.reset_epoch == snapshot.recording_reset_epoch
            && frame.route_epoch == self.route_epoch.load(Ordering::Acquire)
            && (frame.combined
                || (!snapshot.local.deafened
                    && snapshot.master > 0.0
                    && self.audible.load(Ordering::Acquire)
                    && snapshot.recording_peer_epochs.iter().any(|(peer, epoch)| {
                        *epoch == frame.peer_epoch
                            && snapshot.peers.get(peer).is_some_and(|peer| peer.gain > 0.0)
                    })))
    }

    pub fn publish(
        &self,
        snapshot: &GameSnapshot,
        timestamp: u64,
        stem: Option<(&RecordingIdentity, u64)>,
        samples: &[f32],
        connection: u64,
        route_epoch: u64,
    ) {
        self.publishing.fetch_add(1, Ordering::SeqCst);
        let _publishing = Publishing(&self.publishing);
        if connection == 0
            || self.connected.load(Ordering::SeqCst) != connection
            || self.route_epoch.load(Ordering::Acquire) != route_epoch
            || self.failed.load(Ordering::Acquire)
        {
            return;
        }
        let publisher = &self.publisher;
        let Ok(mut frame) = publisher.free.try_recv() else {
            if self.connected.load(Ordering::Acquire) == connection {
                self.failed.store(true, Ordering::Release);
            }
            return;
        };
        let (id, group, name, peer_epoch) = match stem {
            Some((identity, peer_epoch)) => (
                identity.id,
                identity.group,
                identity.name.as_str(),
                peer_epoch,
            ),
            None => (0, 0, "", 0),
        };
        if samples.len() != SAMPLES || name.len() > 192 || samples.iter().any(|v| !v.is_finite()) {
            self.failed.store(true, Ordering::Release);
            let _ = publisher.recycle.try_send(frame);
            return;
        }
        frame.timestamp = timestamp;
        frame.identity = id;
        frame.group = group;
        frame.name_len = name.len();
        frame.name[..name.len()].copy_from_slice(name.as_bytes());
        frame.samples.copy_from_slice(samples);
        frame.reset_epoch = snapshot.recording_reset_epoch;
        frame.peer_epoch = peer_epoch;
        frame.connection = connection;
        frame.route_epoch = route_epoch;
        frame.combined = stem.is_none();
        if let Err(error) = publisher.pending.try_send(frame) {
            let _ = publisher.recycle.try_send(error.into_inner());
            self.failed.store(true, Ordering::Release);
        }
    }

    #[cfg(test)]
    pub(crate) fn test_recycle(&self, frame: Box<AudioFrame>) {
        self.publisher
            .recycle
            .try_send(frame)
            .expect("test frame fits pool");
    }

    #[cfg(test)]
    pub(crate) fn test_connect(&self) -> (u64, Receiver<Box<AudioFrame>>) {
        self.initialize_pool().expect("test recording pools");
        self.failed.store(false, Ordering::Release);
        (self.begin_connection(), self.publisher.queued.clone())
    }
}

struct Publishing<'a>(&'a AtomicUsize);
impl Drop for Publishing<'_> {
    fn drop(&mut self) {
        self.0.fetch_sub(1, Ordering::SeqCst);
    }
}

const SAMPLE_RATE: u64 = 48_000;
const FRAMES: u64 = (SAMPLES / 2) as u64;
const CLOCK_RESYNC_NS: u64 = 100_000_000;

#[derive(Default)]
/// QPC anchor plus 48 kHz sample count; stalls over 100 ms start a new forward segment.
pub(crate) struct RecordingClock {
    connection: u64,
    anchor: u64,
    frames: u64,
}

impl RecordingClock {
    pub fn reset(&mut self) {
        *self = Self::default();
    }

    pub fn next(&mut self, connection: u64, observed: u64) -> Option<u64> {
        if connection == 0 {
            self.reset();
            return None;
        }
        if self.connection != connection {
            self.connection = connection;
            self.anchor = observed;
            self.frames = 0;
        }
        let expected =
            self.anchor + ((self.frames as u128 * 1_000_000_000) / SAMPLE_RATE as u128) as u64;
        let timestamp = if observed.saturating_sub(expected) > CLOCK_RESYNC_NS {
            self.anchor = observed;
            self.frames = 0;
            observed
        } else {
            expected
        };
        self.frames += FRAMES;
        Some(timestamp)
    }
}

pub(crate) struct ObsRecording {
    pub shared: Arc<Shared>,
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl ObsRecording {
    pub fn new() -> Self {
        Self {
            shared: Arc::new(Shared::new()),
            stop: Arc::new(AtomicBool::new(false)),
            thread: None,
        }
    }

    pub fn configure(
        &mut self,
        enabled: bool,
        mode: u8,
        gs: Arc<GameState>,
    ) -> std::io::Result<()> {
        self.shared.mode.store(mode, Ordering::Release);
        if enabled
            && self
                .thread
                .as_ref()
                .is_some_and(|thread| !thread.is_finished())
        {
            return Ok(());
        }
        self.shutdown();
        if !enabled {
            self.shared.status("disabled", "OBS recording disabled");
            return Ok(());
        }
        self.stop.store(false, Ordering::Release);
        self.shared.failed.store(false, Ordering::Release);
        self.shared.initialize_pool()?;
        let pending_rx = self.shared.publisher.queued.clone();
        let free_tx = self.shared.publisher.recycle.clone();
        let shared = self.shared.clone();
        let stop = self.stop.clone();
        self.thread = Some(
            std::thread::Builder::new()
                .name("pc-obs-pipe".into())
                .spawn(move || {
                    if let Err(error) = windows::serve(&shared, &stop, &gs, &pending_rx, &free_tx) {
                        shared.status("error", format!("OBS recording bridge: {error}"));
                    }
                    shared.cancel();
                })?,
        );
        Ok(())
    }

    pub fn shutdown(&mut self) {
        self.shared.cancel();
        self.stop.store(true, Ordering::Release);
        if let Some(thread) = self.thread.take() {
            if thread.join().is_err() {
                self.shared
                    .status("error", "OBS recording bridge thread failed");
            }
        }
        while self.shared.publishing.load(Ordering::SeqCst) != 0 {
            std::thread::yield_now();
        }
        while let Ok(frame) = self.shared.publisher.queued.try_recv() {
            self.shared
                .publisher
                .recycle
                .try_send(frame)
                .expect("retired frame fits pool");
        }
        if self.shared.pool_ready.load(Ordering::Acquire) {
            let available = self.shared.publisher.free.len();
            for _ in 0..available {
                let mut frame = self
                    .shared
                    .publisher
                    .free
                    .try_recv()
                    .expect("retired pool stable");
                *frame = AudioFrame::default();
                self.shared
                    .publisher
                    .recycle
                    .try_send(frame)
                    .expect("retired frame fits pool");
            }
        }
    }
}

impl Drop for ObsRecording {
    fn drop(&mut self) {
        self.shutdown();
    }
}

pub(crate) fn parse_status(body: &[u8]) -> std::io::Result<(&'static str, String)> {
    let invalid = || {
        std::io::Error::new(
            std::io::ErrorKind::InvalidData,
            "invalid OBS status message",
        )
    };
    if body.len() < 4 || body[0] != 3 {
        return Err(invalid());
    }
    let state = match body[1] {
        0 => "ready",
        1 => "recording",
        2 => "paused",
        3 => "error",
        _ => return Err(invalid()),
    };
    let length = u16::from_le_bytes([body[2], body[3]]) as usize;
    if length > 1024 || body.len() != length + 4 {
        return Err(invalid());
    }
    Ok((
        state,
        std::str::from_utf8(&body[4..])
            .map_err(|_| invalid())?
            .to_owned(),
    ))
}

#[path = "obs_recording_windows.rs"]
mod windows;
pub(crate) use windows::qpc_ns;

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn status_is_strict_and_bounded() {
        assert_eq!(
            parse_status(&[3, 1, 2, 0, b'o', b'k']).unwrap(),
            ("recording", "ok".into())
        );
        for bytes in [
            &[3, 4, 0, 0][..],
            &[3, 0, 1, 0][..],
            &[3, 0, 1, 0, 255][..],
            &[3, 0, 0, 0, 0][..],
        ] {
            assert!(parse_status(bytes).is_err());
        }
    }
    #[test]
    fn default_bridge_retains_no_audio() {
        let bridge = ObsRecording::new();
        assert!(!bridge.shared.active());
        assert!(bridge.shared.publisher.free.is_empty());
    }
    #[test]
    fn pool_exhaustion_fails_instead_of_losing_samples() {
        let bridge = Shared::new();
        let (connection, _pending) = bridge.test_connect();
        for _ in 0..=POOL_SIZE {
            bridge.publish(
                &GameSnapshot::default(),
                1,
                None,
                &[0.0; SAMPLES],
                connection,
                bridge.route_epoch.load(Ordering::Acquire),
            );
        }
        assert!(bridge.failed.load(Ordering::Acquire));
        assert!(!bridge.active());
    }
}

#[cfg(test)]
mod lifecycle_tests {
    use super::*;

    #[test]
    fn sample_clock_ignores_dispatch_jitter_and_resyncs_after_stalls() {
        let mut clock = RecordingClock::default();
        let anchor = 1_000_000_000;
        assert_eq!(clock.next(1, anchor), Some(anchor));
        for (index, jitter) in [3_000_000, 500_000, 7_000_000, 0].into_iter().enumerate() {
            let expected = anchor + (index as u64 + 1) * 20_000_000;
            assert_eq!(clock.next(1, expected + jitter), Some(expected));
        }
        let resumed = anchor + 400_000_000;
        assert_eq!(clock.next(1, resumed), Some(resumed));
        assert_eq!(
            clock.next(1, resumed + 25_000_000),
            Some(resumed + 20_000_000)
        );
        assert_eq!(clock.next(0, resumed), None);
        assert_eq!(
            clock.next(2, resumed + 70_000_000),
            Some(resumed + 70_000_000)
        );
    }

    #[test]
    fn cancellation_rejects_inflight_generation_without_failing_bridge() {
        let shared = Shared::new();
        let (old, pending) = shared.test_connect();
        let route = shared.route_epoch.load(Ordering::Acquire);
        shared.cancel();
        let (new, _) = shared.test_connect();
        assert_ne!(old, new);
        shared.publish(
            &GameSnapshot::default(),
            1,
            None,
            &[1.0; SAMPLES],
            old,
            route,
        );
        assert!(pending.is_empty());
        assert!(!shared.failed.load(Ordering::Acquire));
    }

    #[test]
    fn shutdown_recycles_and_erases_queued_audio() {
        let mut bridge = ObsRecording::new();
        let (connection, pending) = bridge.shared.test_connect();
        bridge.shared.publish(
            &GameSnapshot::default(),
            1,
            None,
            &[1.0; SAMPLES],
            connection,
            bridge.shared.route_epoch.load(Ordering::Acquire),
        );
        assert_eq!(pending.len(), 1);
        bridge.shutdown();
        assert!(pending.is_empty());
        assert_eq!(bridge.shared.publisher.free.len(), POOL_SIZE);
        for _ in 0..POOL_SIZE {
            let frame = bridge.shared.publisher.free.try_recv().unwrap();
            assert!(frame.samples.iter().all(|sample| *sample == 0.0));
            bridge.shared.test_recycle(frame);
        }
    }
}

#[cfg(test)]
mod privacy_tests {
    use super::*;
    use crate::gamestate::{LocalState, PeerState, RecordingIdentity};
    use std::collections::HashMap;

    #[test]
    fn queued_frames_cannot_survive_permission_or_identity_epochs() {
        let shared = Shared::new();
        let (connection, pending) = shared.test_connect();
        let gs = GameState::new();
        let peer = || {
            (
                "transport".into(),
                PeerState {
                    gain: 1.0,
                    pan: 0.0,
                    mode: 0,
                    muffled: false,
                },
            )
        };
        let identity = |id| {
            HashMap::from([(
                "transport".into(),
                RecordingIdentity {
                    id,
                    name: "Safe name".into(),
                    group: 1,
                },
            )])
        };
        gs.apply_recording(LocalState::default(), 1.0, vec![peer()], identity(7));
        shared.set_audible(true);
        let route = shared.route_epoch.load(Ordering::Acquire);
        let recording_identity = RecordingIdentity {
            id: 7,
            name: "Safe name".into(),
            group: 1,
        };
        shared.publish(
            &gs.snapshot(),
            1,
            Some((
                &recording_identity,
                gs.snapshot().recording_peer_epochs["transport"],
            )),
            &[0.5; SAMPLES],
            connection,
            route,
        );
        let frame = pending.try_recv().unwrap();
        assert!(shared.frame_authorized(&frame, &gs.snapshot(), connection));
        gs.apply_recording(LocalState::default(), 1.0, vec![peer()], identity(8));
        assert!(!shared.frame_authorized(&frame, &gs.snapshot(), connection));
        shared.test_recycle(frame);
        shared.publish(&gs.snapshot(), 2, None, &[0.5; SAMPLES], connection, route);
        let combined = pending.try_recv().unwrap();
        gs.set_master(0.0);
        gs.set_master(1.0);
        assert!(!shared.frame_authorized(&combined, &gs.snapshot(), connection));
        shared.test_recycle(combined);
    }

    fn two_peers() -> (Shared, u64, Receiver<Box<AudioFrame>>, GameState) {
        let shared = Shared::new();
        let (connection, pending) = shared.test_connect();
        let gs = GameState::new();
        gs.apply(LocalState::default(), 1.0, peers(1.0, false));
        shared.set_audible(true);
        (shared, connection, pending, gs)
    }

    fn peers(a_gain: f32, b_muffled: bool) -> Vec<(String, PeerState)> {
        let peer = |gain, muffled| PeerState {
            gain,
            pan: 0.0,
            mode: 0,
            muffled,
        };
        vec![
            ("a".into(), peer(a_gain, false)),
            ("b".into(), peer(1.0, b_muffled)),
        ]
    }

    fn publish(
        shared: &Shared,
        gs: &GameState,
        connection: u64,
        pending: &Receiver<Box<AudioFrame>>,
        peer: Option<&str>,
    ) -> Box<AudioFrame> {
        let snapshot = gs.snapshot();
        let identity = RecordingIdentity {
            id: 1,
            name: "Alias".into(),
            group: 1,
        };
        shared.publish(
            &snapshot,
            1,
            peer.map(|peer| (&identity, snapshot.recording_peer_epochs[peer])),
            &[0.5; SAMPLES],
            connection,
            shared.route_epoch.load(Ordering::Acquire),
        );
        pending.try_recv().unwrap()
    }

    #[test]
    fn peer_change_drops_only_that_peers_queued_stem() {
        let (shared, connection, pending, gs) = two_peers();
        let a = publish(&shared, &gs, connection, &pending, Some("a"));
        let b = publish(&shared, &gs, connection, &pending, Some("b"));
        let combined = publish(&shared, &gs, connection, &pending, None);
        gs.apply(LocalState::default(), 1.0, peers(1.0, true));
        let snapshot = gs.snapshot();
        assert!(shared.frame_authorized(&a, &snapshot, connection));
        assert!(shared.frame_authorized(&combined, &snapshot, connection));
        assert!(!shared.frame_authorized(&b, &snapshot, connection));
        gs.apply(LocalState::default(), 1.0, peers(0.0, true));
        gs.apply(LocalState::default(), 1.0, peers(1.0, true));
        assert!(!shared.frame_authorized(&a, &gs.snapshot(), connection));
        assert!(shared.frame_authorized(&combined, &gs.snapshot(), connection));
        for frame in [a, b, combined] {
            shared.test_recycle(frame);
        }
    }

    #[test]
    fn deafen_drops_every_queued_frame() {
        let (shared, connection, pending, gs) = two_peers();
        let a = publish(&shared, &gs, connection, &pending, Some("a"));
        let combined = publish(&shared, &gs, connection, &pending, None);
        gs.set_local(LocalState { deafened: true });
        gs.set_local(LocalState { deafened: false });
        assert!(!shared.frame_authorized(&a, &gs.snapshot(), connection));
        assert!(!shared.frame_authorized(&combined, &gs.snapshot(), connection));
        for frame in [a, combined] {
            shared.test_recycle(frame);
        }
    }

    #[test]
    fn audibility_updates_do_not_invalidate_the_current_route() {
        let shared = Shared::new();
        let route = shared.route_epoch.load(Ordering::Acquire);
        shared.set_audible(true);
        shared.set_audible(false);
        assert_eq!(shared.route_epoch.load(Ordering::Acquire), route);
        shared.cancel();
        assert_ne!(shared.route_epoch.load(Ordering::Acquire), route);
    }
}
