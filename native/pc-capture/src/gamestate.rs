use std::collections::HashMap;
use std::sync::Mutex;

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
        self.inner.lock().unwrap().master = master;
    }

    pub fn upsert_peer(&self, peer_id: String, peer: PeerState) {
        self.inner.lock().unwrap().peers.insert(peer_id, peer);
    }

    pub fn remove_peer(&self, peer_id: &str) {
        self.inner.lock().unwrap().peers.remove(peer_id);
    }

    pub fn apply(&self, local: LocalState, master: f32, peers: Vec<(String, PeerState)>) {
        let mut g = self.inner.lock().unwrap();
        g.local = local;
        g.master = master;
        g.peers.clear();
        for (id, p) in peers {
            g.peers.insert(id, p);
        }
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
}
