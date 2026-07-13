use std::collections::HashMap;
use std::sync::Mutex;

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
    pub local: LocalState,
    pub master: f32,
    pub peers: HashMap<String, PeerState>,
}

impl Default for GameSnapshot {
    fn default() -> GameSnapshot {
        GameSnapshot {
            local: LocalState::default(),
            master: 1.0,
            peers: HashMap::new(),
        }
    }
}

pub struct GameState {
    inner: Mutex<GameSnapshot>,
}

impl Default for GameState {
    fn default() -> Self {
        Self::new()
    }
}

impl GameState {
    pub fn new() -> GameState {
        GameState {
            inner: Mutex::new(GameSnapshot::default()),
        }
    }

    pub fn snapshot(&self) -> GameSnapshot {
        self.inner.lock().unwrap().clone()
    }

    pub fn set_local(&self, local: LocalState) {
        self.inner.lock().unwrap().local = local;
    }

    pub fn set_master(&self, master: f32) {
        self.inner.lock().unwrap().master = sanitize_master(master);
    }

    pub fn upsert_peer(&self, peer_id: String, peer: PeerState) {
        self.inner
            .lock()
            .unwrap()
            .peers
            .insert(peer_id, sanitize_peer(peer));
    }

    pub fn remove_peer(&self, peer_id: &str) {
        self.inner.lock().unwrap().peers.remove(peer_id);
    }

    pub fn apply(&self, local: LocalState, master: f32, peers: Vec<(String, PeerState)>) {
        let mut g = self.inner.lock().unwrap();
        g.local = local;
        g.master = sanitize_master(master);
        g.peers.clear();
        for (id, p) in peers {
            g.peers.insert(id, sanitize_peer(p));
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
}
