use std::collections::HashMap;
use std::sync::Mutex;

use crate::mix::FalloffMode;

pub const DEFAULT_MAX_DISTANCE: f32 = 6.0;

#[derive(Debug, Clone)]
pub struct LocalState {
    pub x: f32,
    pub y: f32,
    pub facing: f32,
    pub deafened: bool,
}

impl Default for LocalState {
    fn default() -> LocalState {
        LocalState {
            x: 0.0,
            y: 0.0,
            facing: 0.0,
            deafened: false,
        }
    }
}

#[derive(Debug, Clone)]
pub struct PeerState {
    pub x: f32,
    pub y: f32,
    pub muted: bool,
    pub volume: f32,
    pub role_flags: u32,

    pub mode: i32,

    pub nvol: f32,
}

#[derive(Debug, Clone)]
pub struct GameSnapshot {
    pub local: LocalState,
    pub master: f32,
    pub max_distance: f32,
    pub falloff: FalloffMode,
    pub peers: HashMap<String, PeerState>,
}

impl Default for GameSnapshot {
    fn default() -> GameSnapshot {
        GameSnapshot {
            local: LocalState::default(),
            master: 1.0,
            max_distance: DEFAULT_MAX_DISTANCE,
            falloff: FalloffMode::Smooth,
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

    pub fn set_settings(&self, master: f32, max_distance: f32, falloff: FalloffMode) {
        let mut g = self.inner.lock().unwrap();
        g.master = master;
        g.max_distance = max_distance;
        g.falloff = falloff;
    }

    pub fn upsert_peer(&self, peer_id: String, peer: PeerState) {
        self.inner.lock().unwrap().peers.insert(peer_id, peer);
    }

    pub fn remove_peer(&self, peer_id: &str) {
        self.inner.lock().unwrap().peers.remove(peer_id);
    }

    pub fn apply(
        &self,
        local: LocalState,
        master: f32,
        max_distance: f32,
        falloff: FalloffMode,
        peers: Vec<(String, PeerState)>,
    ) {
        let mut g = self.inner.lock().unwrap();
        g.local = local;
        g.master = master;
        g.max_distance = max_distance;
        g.falloff = falloff;
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
    fn snapshot_reflects_local_and_settings() {
        let gs = GameState::new();
        gs.set_local(LocalState {
            x: 1.0,
            y: 2.0,
            facing: 0.5,
            deafened: true,
        });
        gs.set_settings(0.5, 9.0, FalloffMode::VoiceFocused);
        let s = gs.snapshot();
        assert_eq!(s.local.x, 1.0);
        assert_eq!(s.local.y, 2.0);
        assert!(s.local.deafened);
        assert_eq!(s.master, 0.5);
        assert_eq!(s.max_distance, 9.0);
        assert_eq!(s.falloff, FalloffMode::VoiceFocused);
    }

    #[test]
    fn apply_replaces_peers_atomically() {
        let gs = GameState::new();
        gs.upsert_peer(
            "old".to_string(),
            PeerState {
                x: 0.0,
                y: 0.0,
                muted: false,
                volume: 1.0,
                role_flags: 0,
                mode: 0,
                nvol: 1.0,
            },
        );
        gs.apply(
            LocalState::default(),
            1.0,
            6.0,
            FalloffMode::Linear,
            vec![(
                "new".to_string(),
                PeerState {
                    x: 4.0,
                    y: 5.0,
                    muted: true,
                    volume: 0.5,
                    role_flags: 3,
                    mode: 0,
                    nvol: 1.0,
                },
            )],
        );
        let s = gs.snapshot();
        assert!(!s.peers.contains_key("old"));
        let p = s.peers.get("new").unwrap();
        assert_eq!(p.x, 4.0);
        assert_eq!(p.y, 5.0);
        assert!(p.muted);
        assert_eq!(p.volume, 0.5);
        assert_eq!(p.role_flags, 3);
    }

    #[test]
    fn remove_peer_drops_entry() {
        let gs = GameState::new();
        gs.upsert_peer(
            "p".to_string(),
            PeerState {
                x: 1.0,
                y: 1.0,
                muted: false,
                volume: 1.0,
                role_flags: 0,
                mode: 0,
                nvol: 1.0,
            },
        );
        gs.remove_peer("p");
        assert!(gs.snapshot().peers.is_empty());
    }
}
