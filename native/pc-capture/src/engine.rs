use crate::codec::{OpusCodec, FRAME_SIZE};
use crate::dsp::{Dsp, DspConfig};
use crate::gamestate::{GameState, LocalState, PeerState};
use crate::input::{InputConfig, LevelCadence, PeerLevelCadence, SyntheticTone, TelemetryMailbox};
use crate::mix::{Mixer, PeerJitter};
use crate::proto::{
    level_json, local_candidate_json, local_sdp_json, parse_inbound, peer_levels_json,
    peer_state_json, InboundOp,
};
use crate::rtc::{LocalSignal, RtcEngine};
use std::collections::HashMap;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::mpsc::Receiver;
use std::sync::{Arc, Mutex};
use std::time::Instant;

fn peak(samples: &[f32]) -> f32 {
    samples.iter().fold(0.0f32, |m, &s| m.max(s.abs()))
}

fn initial_dsp_config() -> DspConfig {
    #[cfg(target_os = "android")]
    {
        // Android deliberately uses Unity's capture path without bundled native APM/DF DSP.
        DspConfig {
            aec: false,
            agc: false,
            ns: false,
            hpf: false,
        }
    }
    #[cfg(not(target_os = "android"))]
    {
        DspConfig::default()
    }
}

struct DecodeState {
    decoders: HashMap<String, OpusCodec>,
    last_seq: HashMap<String, u16>,
    generations: HashMap<String, u32>,
    mixer: Mixer,
    jitter: PeerJitter,
    peer_levels: PeerLevelCadence,
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
    telemetry: TelemetryMailbox,
    input: Mutex<InputConfig>,
    synthetic: Mutex<Option<SyntheticTone>>,
    local_level_cadence: Mutex<LevelCadence>,
    level: AtomicU32,
}

impl Engine {
    pub fn new() -> Engine {
        let (sig_tx, sig_rx) = std::sync::mpsc::channel::<LocalSignal>();
        Engine {
            rtc: Arc::new(RtcEngine::new(sig_tx)),
            dsp: Mutex::new(Dsp::new(initial_dsp_config())),
            enc: Mutex::new(OpusCodec::new().ok()),
            dec: Mutex::new(DecodeState {
                decoders: HashMap::new(),
                last_seq: HashMap::new(),
                generations: HashMap::new(),
                mixer: Mixer::new(),
                jitter: PeerJitter::new(),
                peer_levels: PeerLevelCadence::new(Instant::now()),
                stereo: vec![0.0; FRAME_SIZE * 2],
            }),
            gs: Arc::new(GameState::new()),
            sig_rx: Mutex::new(sig_rx),
            pending_signal: Mutex::new(None),
            telemetry: TelemetryMailbox::default(),
            input: Mutex::new(InputConfig::default()),
            synthetic: Mutex::new(None),
            local_level_cadence: Mutex::new(LevelCadence::new(Instant::now())),
            level: AtomicU32::new(0),
        }
    }

    pub fn push_mic(&self, samples: &[f32]) -> f32 {
        if samples.len() != FRAME_SIZE {
            return f32::from_bits(self.level.load(Ordering::Relaxed));
        }
        let mut buf = match self.synthetic.lock().unwrap().as_mut() {
            Some(tone) => tone.fill_frame(0).samples,
            None => samples.to_vec(),
        };
        self.dsp.lock().unwrap().capture(&mut buf);
        let input = *self.input.lock().unwrap();
        input.apply_gain(&mut buf);
        let pk = peak(&buf);
        self.level.store(pk.to_bits(), Ordering::Relaxed);

        if let Some(window_peak) = self
            .local_level_cadence
            .lock()
            .unwrap()
            .observe(Instant::now(), pk)
        {
            self.telemetry
                .publish_local(window_peak, window_peak >= input.vad_threshold);
        }

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
        while let Some((peer, generation, seq, data)) = self.rtc.recv() {
            if state.generations.get(&peer).copied() != Some(generation) {
                state.decoders.remove(&peer);
                state.last_seq.remove(&peer);
                state.jitter.remove(&peer);
                state.peer_levels.remove(&peer);
                state.generations.insert(peer.clone(), generation);
            }
            if !state.decoders.contains_key(&peer) {
                match OpusCodec::new() {
                    Ok(c) => {
                        state.decoders.insert(peer.clone(), c);
                    }
                    Err(_) => continue,
                }
            }
            let last = state.last_seq.get(&peer).copied();

            // Decode + conceal into a local batch first so the &mut borrow of `decoders` is
            // released before we touch `jitter`.
            let (frames, advance) = {
                let codec = state.decoders.get_mut(&peer).unwrap();
                crate::codec::decode_with_concealment(codec, last, seq, &data)
            };
            for f in frames {
                state.peer_levels.observe(&peer, peak(&f));
                state.jitter.push(&peer, f);
            }
            if advance {
                state.last_seq.insert(peer.clone(), seq);
            }
            drained += 1;
            if drained >= 256 {
                break;
            }
        }

        if let Some(levels) = state.peer_levels.take_due(Instant::now()) {
            self.telemetry.publish_peers(levels);
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
        let json = match self.sig_rx.lock().unwrap().try_recv().ok() {
            Some(sig) => match sig {
                LocalSignal::Sdp {
                    peer_id,
                    generation,
                    sdp_type,
                    sdp,
                } => local_sdp_json(&peer_id, generation, &sdp_type, &sdp),
                LocalSignal::Candidate {
                    peer_id,
                    generation,
                    candidate,
                } => local_candidate_json(&peer_id, generation, &candidate),
                LocalSignal::PeerState {
                    peer_id,
                    generation,
                    state,
                } => peer_state_json(&peer_id, generation, &state),
            },
            None => {
                if let Some((peak, speaking)) = self.telemetry.take_local() {
                    level_json(peak, speaking)
                } else {
                    let levels = self.telemetry.take_peers()?;
                    peer_levels_json(&levels)
                }
            }
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
            InboundOp::PeerAdd {
                peer_id,
                offerer,
                relay_only,
                generation,
            } => {
                {
                    let mut dec = self.dec.lock().unwrap();
                    dec.decoders.remove(&peer_id);
                    dec.last_seq.remove(&peer_id);
                    dec.jitter.remove(&peer_id);
                    dec.peer_levels.remove(&peer_id);
                    dec.generations.insert(peer_id.clone(), generation);
                }
                self.rtc.add_peer(peer_id, offerer, relay_only, generation);
            }
            InboundOp::PeerRemove { peer_id } => {
                self.rtc.remove_peer(&peer_id);
                self.gs.remove_peer(&peer_id);
                let mut dec = self.dec.lock().unwrap();
                dec.decoders.remove(&peer_id);
                dec.jitter.remove(&peer_id);
                dec.last_seq.remove(&peer_id);
                dec.generations.remove(&peer_id);
                dec.peer_levels.remove(&peer_id);
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
            InboundOp::SetInput {
                gain,
                vad_threshold,
            } => *self.input.lock().unwrap() = InputConfig::sanitized(gain, vad_threshold),
            InboundOp::SetSynthetic { enabled } => {
                *self.synthetic.lock().unwrap() = enabled.then(SyntheticTone::new);
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
    use std::time::Duration;

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

    #[test]
    fn runtime_input_and_synthetic_controls_emit_bounded_level_telemetry() {
        let e = Engine::new();
        e.control(r#"{"op":"set-dsp","aec":false,"agc":false,"ns":false,"hpf":false}"#);
        e.control(r#"{"op":"set-input","gain":2.0,"vad_threshold":0.0001}"#);
        e.control(r#"{"op":"set-synthetic","enabled":true}"#);

        for _ in 0..7 {
            let peak = e.push_mic(&[0.0; FRAME_SIZE]);
            assert!(peak <= 0.024_01);
            std::thread::sleep(Duration::from_millis(20));
        }
        assert!(e.level() > 0.023);

        let json = e.poll_signal().expect("100ms local level telemetry");
        let value: serde_json::Value = serde_json::from_str(&json).unwrap();
        assert_eq!(value["op"], "level");
        assert_eq!(value["speaking"], true);
        assert!(value["peak"].as_f64().unwrap() <= 0.024_01);
        e.ack_signal();

        e.control(r#"{"op":"set-synthetic","enabled":false}"#);
        let peak = e.push_mic(&[0.01; FRAME_SIZE]);
        assert!((peak - 0.02).abs() < 0.000_01);
    }
}
