use std::collections::HashMap;
use std::sync::{Arc, Mutex};

pub const MAX_MASTER_GAIN: f32 = 2.0;
pub const MAX_PEER_GAIN: f32 = 4.0;

#[derive(Debug, Clone, Default)]
pub struct LocalState {
    pub deafened: bool,
}

#[derive(Debug, Clone)]
pub struct PeerState {
    pub gain: f32,
    pub pan: f32,
    pub mode: i32,
}

#[derive(Debug, Clone)]
pub struct GameSnapshot {
    pub version: u64,
    pub deafen_epoch: u64,
    pub local: LocalState,
    pub master: f32,
    pub peers: HashMap<String, PeerState>,
}

impl Default for GameSnapshot {
    fn default() -> GameSnapshot {
        GameSnapshot {
            version: 0,
            deafen_epoch: 0,
            local: LocalState::default(),
            master: 1.0,
            peers: HashMap::new(),
        }
    }
}

pub struct GameState {
    // Readers clone one Arc instead of cloning the peer HashMap on every 20 ms mix tick. Updates
    // use copy-on-write, so an in-flight mixer keeps an immutable, internally consistent view.
    inner: Mutex<Arc<GameSnapshot>>,
}

impl Default for GameState {
    fn default() -> Self {
        Self::new()
    }
}

impl GameState {
    pub fn new() -> GameState {
        GameState {
            inner: Mutex::new(Arc::new(GameSnapshot::default())),
        }
    }

    pub fn snapshot(&self) -> Arc<GameSnapshot> {
        Arc::clone(&self.inner.lock().unwrap())
    }

    pub fn set_local(&self, local: LocalState) {
        let mut snapshot = self.inner.lock().unwrap();
        let next = Arc::make_mut(&mut snapshot);
        next.version = next.version.wrapping_add(1);
        if local.deafened && !next.local.deafened {
            next.deafen_epoch = next.deafen_epoch.wrapping_add(1);
        }
        next.local = local;
    }

    pub fn set_master(&self, master: f32) {
        let mut snapshot = self.inner.lock().unwrap();
        let next = Arc::make_mut(&mut snapshot);
        next.version = next.version.wrapping_add(1);
        next.master = sanitize_master(master);
    }

    pub fn upsert_peer(&self, peer_id: String, peer: PeerState) {
        let mut snapshot = self.inner.lock().unwrap();
        let next = Arc::make_mut(&mut snapshot);
        next.version = next.version.wrapping_add(1);
        next.peers.insert(peer_id, sanitize_peer(peer));
    }

    pub fn remove_peer(&self, peer_id: &str) {
        let mut snapshot = self.inner.lock().unwrap();
        let next = Arc::make_mut(&mut snapshot);
        if next.peers.remove(peer_id).is_some() {
            next.version = next.version.wrapping_add(1);
        }
    }

    pub fn apply(&self, local: LocalState, master: f32, peers: Vec<(String, PeerState)>) {
        let mut snapshot = self.inner.lock().unwrap();
        let next = Arc::make_mut(&mut snapshot);
        next.version = next.version.wrapping_add(1);
        if local.deafened && !next.local.deafened {
            next.deafen_epoch = next.deafen_epoch.wrapping_add(1);
        }
        next.local = local;
        next.master = sanitize_master(master);
        next.peers.clear();
        for (id, p) in peers {
            next.peers.insert(id, sanitize_peer(p));
        }
    }
}

fn sanitize_master(master: f32) -> f32 {
    if master.is_finite() {
        master.clamp(0.0, MAX_MASTER_GAIN)
    } else {
        1.0
    }
}

fn sanitize_peer(peer: PeerState) -> PeerState {
    PeerState {
        gain: if peer.gain.is_finite() {
            peer.gain.clamp(0.0, MAX_PEER_GAIN)
        } else {
            0.0
        },
        pan: if peer.pan.is_finite() {
            peer.pan.clamp(-1.0, 1.0)
        } else {
            0.0
        },
        mode: peer.mode,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn apply_replaces_peers_atomically() {
        let gs = GameState::new();
        gs.upsert_peer(
            "old".to_string(),
            PeerState {
                gain: 1.0,
                pan: 0.0,
                mode: 0,
            },
        );
        gs.apply(
            LocalState::default(),
            1.0,
            vec![(
                "new".to_string(),
                PeerState {
                    gain: 0.5,
                    pan: 0.3,
                    mode: 2,
                },
            )],
        );
        let s = gs.snapshot();
        assert!(!s.peers.contains_key("old"));
        let p = s.peers.get("new").unwrap();
        assert_eq!(p.gain, 0.5);
        assert_eq!(p.pan, 0.3);
        assert_eq!(p.mode, 2);
    }

    #[test]
    fn remove_peer_drops_entry() {
        let gs = GameState::new();
        gs.upsert_peer(
            "p".to_string(),
            PeerState {
                gain: 1.0,
                pan: 0.0,
                mode: 0,
            },
        );
        gs.remove_peer("p");
        assert!(gs.snapshot().peers.is_empty());
    }

    #[test]
    fn snapshot_reflects_local_and_master() {
        let gs = GameState::new();
        gs.set_local(LocalState { deafened: true });
        gs.set_master(0.5);
        let s = gs.snapshot();
        assert!(s.local.deafened);
        assert_eq!(s.master, 0.5);
    }

    #[test]
    fn deafen_epoch_records_each_transition_into_deafen() {
        let gs = GameState::new();
        assert_eq!(gs.snapshot().deafen_epoch, 0);

        gs.set_local(LocalState { deafened: false });
        assert_eq!(gs.snapshot().deafen_epoch, 0);
        gs.set_local(LocalState { deafened: true });
        assert_eq!(gs.snapshot().deafen_epoch, 1);
        gs.set_local(LocalState { deafened: true });
        assert_eq!(gs.snapshot().deafen_epoch, 1);
        gs.set_local(LocalState { deafened: false });
        assert_eq!(gs.snapshot().deafen_epoch, 1);

        gs.apply(LocalState { deafened: true }, 1.0, Vec::new());
        assert_eq!(gs.snapshot().deafen_epoch, 2);
    }

    #[test]
    fn invalid_and_excessive_mix_values_are_sanitized() {
        let gs = GameState::new();
        gs.apply(
            LocalState { deafened: false },
            f32::NAN,
            vec![
                (
                    "bad".to_string(),
                    PeerState {
                        gain: f32::INFINITY,
                        pan: f32::NAN,
                        mode: 0,
                    },
                ),
                (
                    "boost".to_string(),
                    PeerState {
                        gain: 99.0,
                        pan: 2.0,
                        mode: 0,
                    },
                ),
            ],
        );

        let snapshot = gs.snapshot();
        assert_eq!(snapshot.master, 1.0);
        assert_eq!(snapshot.peers["bad"].gain, 0.0);
        assert_eq!(snapshot.peers["bad"].pan, 0.0);
        assert_eq!(snapshot.peers["boost"].gain, MAX_PEER_GAIN);
        assert_eq!(snapshot.peers["boost"].pan, 1.0);
    }

    #[test]
    fn snapshots_share_immutable_storage_until_a_versioned_update() {
        let gs = GameState::new();
        let first = gs.snapshot();
        let second = gs.snapshot();
        assert!(Arc::ptr_eq(&first, &second));
        assert_eq!(first.version, 0);

        gs.set_master(0.5);
        let third = gs.snapshot();
        assert!(!Arc::ptr_eq(&first, &third));
        assert_eq!(
            first.master, 1.0,
            "an in-flight snapshot must stay immutable"
        );
        assert_eq!(third.master, 0.5);
        assert_eq!(third.version, 1);
    }
}
