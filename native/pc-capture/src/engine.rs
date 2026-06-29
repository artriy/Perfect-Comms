use crate::codec::{OpusCodec, FRAME_SIZE};
use crate::dsp::{Dsp, DspConfig};
use crate::gamestate::{GameState, LocalState, PeerState};
use crate::mix::{Mixer, PeerJitter};
use crate::proto::{
    local_candidate_json, local_sdp_json, parse_inbound, peer_state_json, InboundOp,
};
use crate::rtc::{LocalSignal, RtcEngine};
use std::collections::HashMap;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::mpsc::Receiver;
use std::sync::{Arc, Mutex};

fn peak(samples: &[f32]) -> f32 {
    samples.iter().fold(0.0f32, |m, &s| m.max(s.abs()))
}

struct DecodeState {
    decoders: HashMap<String, OpusCodec>,
    mixer: Mixer,
    jitter: PeerJitter,
    stereo: Vec<f32>,
}

pub struct Engine {
    rtc: Arc<RtcEngine>,
    dsp: Mutex<Dsp>,
    enc: Mutex<Option<OpusCodec>>,
    dec: Mutex<DecodeState>,
    gs: Arc<GameState>,
    sig_rx: Mutex<Receiver<LocalSignal>>,

    pending_signal: Mutex<Option<String>>,
    level: AtomicU32,
}

impl Engine {
    pub fn new() -> Engine {
        let (sig_tx, sig_rx) = std::sync::mpsc::channel::<LocalSignal>();
        Engine {
            rtc: Arc::new(RtcEngine::new(sig_tx)),
            dsp: Mutex::new(Dsp::new(DspConfig::default())),
            enc: Mutex::new(OpusCodec::new().ok()),
            dec: Mutex::new(DecodeState {
                decoders: HashMap::new(),
                mixer: Mixer::new(),
                jitter: PeerJitter::new(),
                stereo: vec![0.0; FRAME_SIZE * 2],
            }),
            gs: Arc::new(GameState::new()),
            sig_rx: Mutex::new(sig_rx),
            pending_signal: Mutex::new(None),
            level: AtomicU32::new(0),
        }
    }

    pub fn push_mic(&self, samples: &[f32]) -> f32 {
        if samples.len() != FRAME_SIZE {
            return f32::from_bits(self.level.load(Ordering::Relaxed));
        }
        let mut buf = samples.to_vec();
        self.dsp.lock().unwrap().capture(&mut buf);
        let pk = peak(&buf);
        self.level.store(pk.to_bits(), Ordering::Relaxed);

        let pkt = match self.enc.lock().unwrap().as_mut() {
            Some(enc) => enc.encode(&buf),
            None => Vec::new(),
        };
        if !pkt.is_empty() {
            self.rtc.send_opus(&pkt);
        }
        pk
    }

    pub fn pull_playback(&self, out: &mut [f32]) -> usize {
        let mut state = self.dec.lock().unwrap();

        let mut drained = 0;
        while let Some((peer, data)) = self.rtc.recv() {
            if !state.decoders.contains_key(&peer) {
                match OpusCodec::new() {
                    Ok(c) => {
                        state.decoders.insert(peer.clone(), c);
                    }
                    Err(_) => continue,
                }
            }
            let codec = state.decoders.get_mut(&peer).unwrap();
            let mut pcm = [0f32; FRAME_SIZE];
            let n = codec.decode(&data, &mut pcm);
            if n > 0 {
                state.jitter.push(&peer, pcm[..n].to_vec());
            }
            drained += 1;
            if drained >= 256 {
                break;
            }
        }

        let round = state.jitter.playout_round();
        if round.is_empty() {
            return 0;
        }
        let per_peer: Vec<(String, &[f32])> = round
            .iter()
            .map(|(k, v)| (k.clone(), v.as_slice()))
            .collect();
        let DecodeState { mixer, stereo, .. } = &mut *state;
        mixer.mix(&per_peer, &self.gs, stereo);
        self.dsp.lock().unwrap().far_end(stereo);
        let n = out.len().min(stereo.len());
        out[..n].copy_from_slice(&stereo[..n]);
        n
    }

    pub fn level(&self) -> f32 {
        f32::from_bits(self.level.load(Ordering::Relaxed))
    }

    pub fn poll_signal(&self) -> Option<String> {
        {
            let pending = self.pending_signal.lock().unwrap();
            if pending.is_some() {
                return pending.clone();
            }
        }
        let sig = self.sig_rx.lock().unwrap().try_recv().ok()?;
        let json = match sig {
            LocalSignal::Sdp {
                peer_id,
                sdp_type,
                sdp,
            } => local_sdp_json(&peer_id, &sdp_type, &sdp),
            LocalSignal::Candidate { peer_id, candidate } => {
                local_candidate_json(&peer_id, &candidate)
            }
            LocalSignal::PeerState { peer_id, state } => peer_state_json(&peer_id, &state),
        };
        *self.pending_signal.lock().unwrap() = Some(json.clone());
        Some(json)
    }

    pub fn ack_signal(&self) {
        *self.pending_signal.lock().unwrap() = None;
    }

    pub fn control(&self, json: &str) {
        let op = match parse_inbound(json) {
            Ok(op) => op,
            Err(_) => return,
        };
        match op {
            InboundOp::SetIceServers { servers } => self.rtc.set_ice_servers(&servers),
            InboundOp::PeerAdd { peer_id, offerer } => self.rtc.add_peer(peer_id, offerer),
            InboundOp::PeerRemove { peer_id } => {
                self.rtc.remove_peer(&peer_id);
                self.gs.remove_peer(&peer_id);
                let mut dec = self.dec.lock().unwrap();
                dec.decoders.remove(&peer_id);
                dec.jitter.remove(&peer_id);
            }
            InboundOp::SetRemoteSdp {
                peer_id,
                sdp_type,
                sdp,
            } => self.rtc.set_remote_sdp(&peer_id, &sdp_type, &sdp),
            InboundOp::AddIceCandidate { peer_id, candidate } => {
                self.rtc.add_ice_candidate(&peer_id, &candidate)
            }
            InboundOp::SetDsp { aec, agc, ns, hpf } => {
                self.dsp
                    .lock()
                    .unwrap()
                    .set(DspConfig { aec, agc, ns, hpf })
            }
            InboundOp::GameState {
                deaf,
                master,
                peers,
            } => {
                let local = LocalState { deafened: deaf };
                let peer_states: Vec<(String, PeerState)> = peers
                    .into_iter()
                    .map(|p| {
                        (
                            p.id,
                            PeerState {
                                gain: p.gain,
                                pan: p.pan,
                                mode: p.mode,
                            },
                        )
                    })
                    .collect();
                self.gs.apply(local, master, peer_states);
            }
            InboundOp::Hello { .. }
            | InboundOp::SelectDevice { .. }
            | InboundOp::SelectOutputDevice { .. }
            | InboundOp::Start
            | InboundOp::Stop
            | InboundOp::Ping => {}
        }
    }
}

impl Default for Engine {
    fn default() -> Self {
        Engine::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn push_mic_wrong_size_is_noop_and_keeps_level() {
        let e = Engine::new();

        let lvl = e.push_mic(&[0.1f32; 100]);
        assert_eq!(lvl, 0.0);
    }

    #[test]
    fn pull_playback_with_no_peers_returns_zero() {
        let e = Engine::new();
        let mut out = vec![0f32; FRAME_SIZE * 2];
        assert_eq!(e.pull_playback(&mut out), 0);
    }

    #[test]
    fn control_ignores_garbage_and_desktop_ops() {
        let e = Engine::new();
        e.control("not json");
        e.control(r#"{"op":"ping"}"#);
        e.control(r#"{"op":"start"}"#);

        e.control(r#"{"op":"game-state","lx":0,"ly":0,"facing":0,"deaf":false,"master":1.0,"maxd":5.0,"falloff":0,"peers":[]}"#);
    }

    #[test]
    fn poll_signal_empty_when_idle() {
        let e = Engine::new();
        assert!(e.poll_signal().is_none());
    }
}
