use crate::codec::{
    decode_with_media_gap_report, EncodedPacketBuffer, EncodedRtpPacket, OpusCodec, FRAME_SIZE,
    MAX_CONCEAL_FRAMES,
};
use crate::dsp::{Dsp, DspConfig};
use crate::gamestate::{GameState, LocalState, PeerState};
use crate::input::{
    InputConfig, LevelCadence, NoiseGate, PeerLevelCadence, SyntheticTone, TelemetryMailbox,
};
use crate::mix::{Mixer, PeerJitter};
use crate::proto::{
    level_json, local_candidate_json, local_sdp_json, parse_inbound, peer_levels_json,
    peer_state_json, InboundOp, MediaReceiveStats,
};
use crate::rtc::{LocalSignal, PeerNetworkPathSnapshot, RtcEngine};
use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::mpsc::Receiver;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

const MOBILE_DIAGNOSTICS_INTERVAL: Duration = Duration::from_secs(2);

fn peak(samples: &[f32]) -> f32 {
    samples.iter().fold(0.0f32, |m, &s| m.max(s.abs()))
}

fn initial_dsp_config() -> DspConfig {
    effective_dsp_config(DspConfig::default())
}

#[cfg(any(target_os = "android", test))]
fn android_dsp_config(_requested: DspConfig) -> DspConfig {
    // Android deliberately uses Unity's platform capture path without the bundled native APM.
    // Keep this separate from cfg selection so host tests can lock the Android policy down.
    DspConfig {
        aec: false,
        agc: false,
        ns: false,
        ns_very_high: false,
        hpf: false,
    }
}

fn effective_dsp_config(requested: DspConfig) -> DspConfig {
    #[cfg(target_os = "android")]
    {
        android_dsp_config(requested)
    }
    #[cfg(not(target_os = "android"))]
    {
        requested
    }
}

struct DecodeState {
    decoders: HashMap<String, OpusCodec>,
    last_seq: HashMap<String, u16>,
    encoded: HashMap<String, EncodedPacketBuffer>,
    generations: HashMap<String, u32>,
    mixer: Mixer,
    jitter: PeerJitter,
    peer_levels: PeerLevelCadence,
    stereo: Vec<f32>,
}

pub(crate) fn reset_decoded_peer_timeline(
    decoders: &mut HashMap<String, OpusCodec>,
    last_seq: &mut HashMap<String, u16>,
    jitter: &mut PeerJitter,
    peer: &str,
) -> Result<(), crate::opus_native::OpusError> {
    // An encoded-buffer fast-forward starts a new decoder timeline. Any staged/catch-up PCM was
    // decoded against the old sequence history and must not play in front of the restart packet.
    last_seq.remove(peer);
    jitter.reset_peer(peer);
    if let Some(codec) = decoders.get_mut(peer) {
        codec.reset_decoder()?;
    }
    Ok(())
}

pub struct Engine {
    rtc: Arc<RtcEngine>,
    dsp: Mutex<Dsp>,
    enc: Mutex<OpusCodec>,
    // Outermost transmit lock. Both microphone processing and encoder-history resets acquire this
    // before `enc`, so no direct FFI caller can overlap a capture with a privacy boundary.
    capture_pipeline: Mutex<()>,
    dec: Mutex<DecodeState>,
    gs: Arc<GameState>,
    sig_rx: Mutex<Receiver<LocalSignal>>,

    pending_signal: Mutex<Option<String>>,
    telemetry: TelemetryMailbox,
    input: Mutex<InputConfig>,
    noise_gate: Mutex<NoiseGate>,
    synthetic: Mutex<Option<SyntheticTone>>,
    mic_scratch: Mutex<Box<[f32; FRAME_SIZE]>>,
    diagnostics: Mutex<MobileDiagnosticsState>,
    local_level_cadence: Mutex<LevelCadence>,
    level: AtomicU32,
    mic_active: AtomicBool,
    encoder_epoch: AtomicU64,
    encoder_policy_generation: AtomicU64,
}

impl Engine {
    pub fn new() -> Engine {
        Self::try_new().expect("pc-capture: Opus encoder/decoder initialization failed")
    }

    /// Constructs an engine only when its transmit codec is usable. Mobile callers surface this
    /// failure as a null FFI handle so managed recovery can retry; a healthy-looking engine with no
    /// encoder would otherwise negotiate peers and emit speaking levels while sending no RTP.
    pub fn try_new() -> Result<Engine, crate::opus_native::OpusError> {
        let (sig_tx, sig_rx) = std::sync::mpsc::channel::<LocalSignal>();
        let encoder = OpusCodec::new()?;
        Ok(Engine {
            rtc: Arc::new(RtcEngine::new(sig_tx)),
            dsp: Mutex::new(Dsp::new(initial_dsp_config())),
            enc: Mutex::new(encoder),
            capture_pipeline: Mutex::new(()),
            dec: Mutex::new(DecodeState {
                decoders: HashMap::new(),
                last_seq: HashMap::new(),
                encoded: HashMap::new(),
                generations: HashMap::new(),
                mixer: Mixer::new(),
                // Network variance is handled while packets are still encoded. This queue only
                // stages decoded/FEC/PLC frames for the mixer and must not add a second adaptive
                // 40-300 ms delay.
                jitter: PeerJitter::with_staging_limits(1, 8),
                peer_levels: PeerLevelCadence::new(Instant::now()),
                stereo: vec![0.0; FRAME_SIZE * 2],
            }),
            gs: Arc::new(GameState::new()),
            sig_rx: Mutex::new(sig_rx),
            pending_signal: Mutex::new(None),
            telemetry: TelemetryMailbox::default(),
            input: Mutex::new(InputConfig::default()),
            noise_gate: Mutex::new(NoiseGate::default()),
            synthetic: Mutex::new(None),
            mic_scratch: Mutex::new(Box::new([0.0; FRAME_SIZE])),
            diagnostics: Mutex::new(MobileDiagnosticsState::default()),
            local_level_cadence: Mutex::new(LevelCadence::new(Instant::now())),
            level: AtomicU32::new(0),
            mic_active: AtomicBool::new(false),
            encoder_epoch: AtomicU64::new(0),
            encoder_policy_generation: AtomicU64::new(0),
        })
    }

    pub fn transport_ready(&self) -> bool {
        self.rtc.transport_ready()
    }

    pub fn transport_error(&self) -> Option<&str> {
        self.rtc.transport_error()
    }

    fn reset_encoder_history(&self) -> u64 {
        let _pipeline = self.capture_pipeline.lock().unwrap();
        let mut encoder = self.enc.lock().unwrap();
        if let Err(error) = encoder.reset_encoder() {
            panic!("pc-capture: Opus encoder privacy reset failed: {error}");
        }
        let epoch = self
            .encoder_epoch
            .load(Ordering::Relaxed)
            .checked_add(1)
            .expect("pc-capture: encoder privacy epoch exhausted");
        assert!(
            self.rtc.advance_encoder_epoch(epoch),
            "pc-capture: RTP privacy drain timed out"
        );
        self.encoder_epoch.store(epoch, Ordering::Release);
        epoch
    }

    pub fn push_mic(&self, samples: &[f32]) -> f32 {
        self.push_mic_with_media_gap(samples, 0)
    }

    pub fn push_mic_with_media_gap(&self, samples: &[f32], skipped_before_current: u64) -> f32 {
        if samples.len() != FRAME_SIZE {
            return f32::from_bits(self.level.load(Ordering::Relaxed));
        }
        // Lock order is always capture_pipeline -> enc. Keep this guard through send_opus so a
        // concurrent PeerAdd/Start/Stop cannot reset and then be followed by stale PCM encoding.
        let _pipeline = self.capture_pipeline.lock().unwrap();
        if !self.mic_active.load(Ordering::Acquire) {
            return f32::from_bits(self.level.load(Ordering::Relaxed));
        }
        // The FFI capture thread is the only normal producer, but retain a mutex so accidental
        // concurrent callers remain memory-safe and serialize exactly as the encoder already did.
        // The fixed-size scratch buffer never reallocates on the normal microphone path.
        let mut buf = self.mic_scratch.lock().unwrap();
        if let Some(tone) = self.synthetic.lock().unwrap().as_mut() {
            let synthetic = tone.fill_frame(0, 0, false);
            buf.copy_from_slice(&synthetic.samples);
        } else {
            buf.copy_from_slice(samples);
        }
        self.dsp.lock().unwrap().capture(&mut buf[..]);
        let input = *self.input.lock().unwrap();
        input.apply_gain(&mut buf[..]);
        let pk = peak(&buf[..]);
        self.noise_gate
            .lock()
            .unwrap()
            .process(&mut buf[..], input.noise_gate_threshold);

        let policy = self.rtc.encoder_policy_snapshot();
        let mut encoder = self.enc.lock().unwrap();
        // Stop closes this gate before waiting on the encoder mutex, then resets history while
        // holding it. A frame already being processed can therefore never encode after Stop or
        // rebuild DRED history between the reset and the next authorized Start.
        if !self.mic_active.load(Ordering::Acquire) {
            return f32::from_bits(self.level.load(Ordering::Relaxed));
        }
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
        self.rtc.record_capture_media_gap(
            skipped_before_current,
            if skipped_before_current <= MAX_CONCEAL_FRAMES as u64 {
                skipped_before_current
            } else {
                0
            },
            u64::from(skipped_before_current > MAX_CONCEAL_FRAMES as u64),
        );
        if policy.generation != self.encoder_policy_generation.load(Ordering::Relaxed)
            && encoder
                .set_network_conditions(policy.packet_loss_percent, policy.bitrate)
                .is_ok()
        {
            self.encoder_policy_generation
                .store(policy.generation, Ordering::Relaxed);
        }
        if skipped_before_current > MAX_CONCEAL_FRAMES as u64 {
            encoder
                .reset_encoder()
                .expect("pc-capture: Opus encoder discontinuity reset failed");
        } else {
            let silence = [0.0f32; FRAME_SIZE];
            for _ in 0..skipped_before_current {
                assert!(
                    !encoder.encode(&silence).is_empty(),
                    "pc-capture: Opus encoder produced no placeholder packet"
                );
            }
        }
        let pkt = encoder.encode(&buf[..]);
        // DTX is disabled, so every valid 20 ms frame must produce a packet. Treat an encoder
        // failure as fatal at the FFI catch boundary instead of running forever as silent RTP.
        assert!(
            !pkt.is_empty(),
            "pc-capture: Opus encoder produced no packet"
        );
        let packet_encoder_epoch = self.encoder_epoch.load(Ordering::Acquire);
        self.rtc
            .send_opus_with_media_gap(&pkt, packet_encoder_epoch, skipped_before_current);
        pk
    }

    pub fn pull_playback(&self, out: &mut [f32]) -> usize {
        let mut state = self.dec.lock().unwrap();

        let mut drained = 0;
        while let Some(packet) = self.rtc.recv() {
            let peer = packet.peer_id;
            if state.generations.get(&peer).copied() != Some(packet.generation) {
                state.decoders.remove(&peer);
                state.last_seq.remove(&peer);
                state.encoded.remove(&peer);
                state.jitter.remove(&peer);
                state.peer_levels.remove(&peer);
                state.generations.insert(peer.clone(), packet.generation);
            }
            let media = self.rtc.media_receive_counters();
            state
                .encoded
                .entry(peer.clone())
                .or_insert_with(|| EncodedPacketBuffer::new(peer.clone(), media))
                .insert(EncodedRtpPacket {
                    sequence: packet.sequence,
                    timestamp: packet.timestamp,
                    arrival: packet.arrival,
                    payload: packet.payload,
                });
            drained += 1;
            if drained >= 256 {
                break;
            }
        }

        let now = Instant::now();
        let peers: Vec<String> = state.encoded.keys().cloned().collect();
        for peer in peers {
            let Some(packet) = state
                .encoded
                .get_mut(&peer)
                .and_then(|buffer| buffer.pop_ready(now))
            else {
                continue;
            };
            if packet.reset_decoder {
                let DecodeState {
                    decoders,
                    last_seq,
                    jitter,
                    ..
                } = &mut *state;
                if let Err(error) = reset_decoded_peer_timeline(decoders, last_seq, jitter, &peer) {
                    panic!("pc-capture: Opus decoder reset failed: {error}");
                }
            }
            if !state.decoders.contains_key(&peer) {
                match OpusCodec::new() {
                    Ok(codec) => {
                        state.decoders.insert(peer.clone(), codec);
                    }
                    Err(error) => panic!("pc-capture: Opus decoder initialization failed: {error}"),
                }
            }
            let last = state.last_seq.get(&peer).copied();
            let (frames, advance, report) = {
                let codec = state.decoders.get_mut(&peer).unwrap();
                decode_with_media_gap_report(
                    codec,
                    last,
                    packet.sequence,
                    packet.local_media_gap_before,
                    &packet.payload,
                )
            };
            if let Some(buffer) = state.encoded.get(&peer) {
                buffer.record_decode(report);
            }
            let recovered_frames = report.dred_frames + report.fec_frames + report.plc_frames;
            for frame in &frames {
                state.peer_levels.observe(&peer, peak(frame));
            }
            state.jitter.push_batch(&peer, frames, recovered_frames);
            if advance {
                state.last_seq.insert(peer, packet.sequence);
            }
        }

        if let Some(levels) = state.peer_levels.take_due(Instant::now()) {
            self.telemetry.publish_peers(levels);
        }

        let round = state.jitter.playout_round();
        let needs_idle_mix = state.mixer.needs_idle_mix();
        if round.is_empty() && !needs_idle_mix {
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

    pub fn encoder_policy_snapshot(&self) -> crate::codec::EncoderPolicySnapshot {
        self.rtc.encoder_policy_snapshot()
    }

    fn poll_mobile_diagnostics(&self) -> Option<String> {
        let now = Instant::now();
        {
            let mut diagnostics = self.diagnostics.lock().unwrap();
            if !diagnostics.enabled || diagnostics.next_emit.is_some_and(|next| now < next) {
                return None;
            }
            diagnostics.next_emit = Some(now + MOBILE_DIAGNOSTICS_INTERVAL);
        }
        // The RTC worker refreshes network-path telemetry independently. This only reads its
        // cached snapshot on the low-frequency poll thread, never from capture or Unity audio.
        Some(mobile_diagnostics_json(
            self.rtc.media_receive_snapshot(),
            self.rtc.network_path_snapshots(),
            self.rtc.encoder_policy_snapshot(),
            self.rtc.native_transport_snapshot(),
        ))
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
                } else if let Some(levels) = self.telemetry.take_peers() {
                    peer_levels_json(&levels)
                } else {
                    self.poll_mobile_diagnostics()?
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
                // Expand the authorized receiver set only after synchronously clearing DRED and
                // advancing the epoch that the RTC track will require.
                let min_encoder_epoch = self.reset_encoder_history();
                {
                    let mut dec = self.dec.lock().unwrap();
                    dec.decoders.remove(&peer_id);
                    dec.last_seq.remove(&peer_id);
                    dec.encoded.remove(&peer_id);
                    dec.jitter.remove(&peer_id);
                    dec.peer_levels.remove(&peer_id);
                    dec.generations.insert(peer_id.clone(), generation);
                }
                self.rtc
                    .add_peer(peer_id, offerer, relay_only, generation, min_encoder_epoch);
            }
            InboundOp::PeerRemove {
                peer_id,
                generation,
            } => {
                self.rtc.remove_peer(&peer_id, generation);
                self.gs.remove_peer(&peer_id);
                let mut dec = self.dec.lock().unwrap();
                dec.decoders.remove(&peer_id);
                dec.jitter.remove(&peer_id);
                dec.last_seq.remove(&peer_id);
                dec.encoded.remove(&peer_id);
                dec.generations.remove(&peer_id);
                dec.peer_levels.remove(&peer_id);
            }
            InboundOp::SetRemoteSdp {
                peer_id,
                generation,
                sdp_type,
                sdp,
            } => {
                self.rtc
                    .set_remote_sdp(&peer_id, generation, &sdp_type, &sdp);
            }
            InboundOp::AddIceCandidate {
                peer_id,
                generation,
                candidate,
            } => {
                self.rtc.add_ice_candidate(&peer_id, generation, &candidate);
            }
            InboundOp::RestartIce {
                peer_id,
                generation,
                relay_only,
                create_offer,
            } => {
                self.rtc
                    .restart_ice(&peer_id, generation, relay_only, create_offer);
            }
            InboundOp::SetDsp {
                aec,
                agc,
                ns,
                ns_very_high,
                hpf,
            } => self
                .dsp
                .lock()
                .unwrap()
                .set(effective_dsp_config(DspConfig {
                    aec,
                    agc,
                    ns,
                    ns_very_high: ns && ns_very_high,
                    hpf,
                })),
            InboundOp::SetDiagnostics { enabled } => {
                let mut diagnostics = self.diagnostics.lock().unwrap();
                if enabled && !diagnostics.enabled {
                    // Emit an immediate baseline, then settle onto the two-second cadence.
                    diagnostics.next_emit = None;
                }
                diagnostics.enabled = enabled;
                if !enabled {
                    diagnostics.next_emit = None;
                }
            }
            InboundOp::SetInput {
                gain,
                vad_threshold,
                noise_gate_threshold,
            } => {
                *self.input.lock().unwrap() =
                    InputConfig::sanitized_with_gate(gain, vad_threshold, noise_gate_threshold)
            }
            InboundOp::SetSynthetic { enabled } => {
                *self.synthetic.lock().unwrap() = enabled.then(SyntheticTone::new);
            }
            InboundOp::Start => {
                // Start remains closed until the reset succeeds. A panic is caught by pc-mobile
                // and marks the engine unhealthy, which is safer than retaining prior speech.
                self.reset_encoder_history();
                self.level.store(0, Ordering::Relaxed);
                self.mic_active.store(true, Ordering::Release);
            }
            InboundOp::Stop => {
                self.mic_active.store(false, Ordering::Release);
                self.reset_encoder_history();
                self.level.store(0, Ordering::Relaxed);
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
            | InboundOp::SetMonitor { .. }
            | InboundOp::Warm
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
    fn push_mic_reuses_fixed_scratch_storage_on_normal_path() {
        let e = Engine::new();
        e.control(r#"{"op":"start"}"#);
        let before = e.mic_scratch.lock().unwrap().as_ptr();
        for value in [0.0f32, 0.01, -0.02, 0.05] {
            let level = e.push_mic(&[value; FRAME_SIZE]);
            assert!(level.is_finite());
        }
        let after = e.mic_scratch.lock().unwrap().as_ptr();
        assert_eq!(before, after);
        assert_eq!(e.mic_scratch.lock().unwrap().len(), FRAME_SIZE);
    }

    #[test]
    fn pull_playback_with_no_peers_returns_zero() {
        let e = Engine::new();
        let mut out = vec![0f32; FRAME_SIZE * 2];
        assert_eq!(e.pull_playback(&mut out), 0);
    }

    #[test]
    fn decoder_timeline_reset_discards_staged_recovery_audio() {
        let mut decoders = HashMap::new();
        decoders.insert("peer".to_string(), OpusCodec::new().expect("decoder"));
        let mut last_seq = HashMap::from([("peer".to_string(), 42u16)]);
        let mut jitter = PeerJitter::with_staging_limits(1, 8);
        jitter.push_batch("peer", vec![vec![0.1; FRAME_SIZE]; 4], 3);
        assert!(!jitter.is_idle());

        reset_decoded_peer_timeline(&mut decoders, &mut last_seq, &mut jitter, "peer")
            .expect("decoder timeline reset");

        assert!(!last_seq.contains_key("peer"));
        assert!(jitter.is_idle());
        assert!(jitter.playout_round().is_empty());
    }

    #[test]
    fn mobile_engine_reorders_encoded_rtp_before_opus_decode() {
        let engine = Engine::new();
        engine.control(r#"{"op":"game-state","lx":0,"ly":0,"facing":0,"deaf":false,"master":1.0,"maxd":5.0,"falloff":0,"peers":[{"id":"peer","gain":1.0,"pan":0.0,"mode":0}]}"#);
        let mut encoder = OpusCodec::new().expect("test encoder");
        let mut frames = Vec::new();
        for frame_index in 0..3 {
            let mut pcm = [0.0f32; FRAME_SIZE];
            for (sample_index, sample) in pcm.iter_mut().enumerate() {
                let phase = (frame_index * FRAME_SIZE + sample_index) as f32;
                *sample = (phase * 2.0 * std::f32::consts::PI * 440.0 / 48_000.0).sin() * 0.2;
            }
            frames.push(encoder.encode(&pcm));
        }

        let start = Instant::now();
        engine.rtc.inject_received_for_test(
            "peer",
            7,
            101,
            crate::codec::RTP_FRAME_TICKS,
            start,
            frames[1].clone(),
        );
        engine.rtc.inject_received_for_test(
            "peer",
            7,
            100,
            0,
            start + Duration::from_millis(5),
            frames[0].clone(),
        );

        let mut output = [0.0f32; FRAME_SIZE * 2];
        assert_eq!(engine.pull_playback(&mut output), output.len());
        engine.rtc.inject_received_for_test(
            "peer",
            7,
            102,
            crate::codec::RTP_FRAME_TICKS * 2,
            start + Duration::from_millis(20),
            frames[2].clone(),
        );
        assert_eq!(engine.pull_playback(&mut output), output.len());

        let stats = engine.rtc.media_receive_snapshot();
        assert_eq!(stats.reordered_recovered, 1);
        assert_eq!(stats.deadline_losses, 0);
        assert!(output.iter().any(|sample| sample.abs() > 0.000_001));
    }

    #[test]
    fn control_ignores_garbage_and_irrelevant_desktop_ops() {
        let e = Engine::new();
        e.control("not json");
        e.control(r#"{"op":"ping"}"#);

        e.control(r#"{"op":"game-state","lx":0,"ly":0,"facing":0,"deaf":false,"master":1.0,"maxd":5.0,"falloff":0,"peers":[]}"#);
    }

    #[test]
    fn mobile_mic_boundaries_are_fail_closed_and_advance_privacy_epoch() {
        let e = Engine::new();
        assert!(!e.mic_active.load(Ordering::Acquire));
        assert_eq!(e.encoder_epoch.load(Ordering::Acquire), 0);
        assert_eq!(e.push_mic(&[0.1; FRAME_SIZE]), 0.0);

        e.control(r#"{"op":"start"}"#);
        assert!(e.mic_active.load(Ordering::Acquire));
        assert_eq!(e.encoder_epoch.load(Ordering::Acquire), 1);
        assert!(e.push_mic(&[0.01; FRAME_SIZE]) > 0.0);

        e.control(r#"{"op":"stop"}"#);
        assert!(!e.mic_active.load(Ordering::Acquire));
        assert_eq!(e.encoder_epoch.load(Ordering::Acquire), 2);
        assert_eq!(e.level(), 0.0);
        assert_eq!(e.push_mic(&[0.1; FRAME_SIZE]), 0.0);

        e.control(
            r#"{"op":"peer-add","peer_id":"privacy-peer","offerer":false,"relay_only":false,"generation":1}"#,
        );
        assert_eq!(e.encoder_epoch.load(Ordering::Acquire), 3);
        assert!(!e.mic_active.load(Ordering::Acquire));
    }

    #[test]
    fn mobile_capture_and_privacy_reset_share_the_outer_pipeline_lock() {
        let engine = Arc::new(Engine::new());
        engine.control(r#"{"op":"start"}"#);

        let held = engine.capture_pipeline.lock().unwrap();
        let (push_tx, push_rx) = std::sync::mpsc::channel();
        let pushing = engine.clone();
        let push = std::thread::spawn(move || {
            let _ = push_tx.send(pushing.push_mic(&[0.01; FRAME_SIZE]));
        });
        assert!(push_rx.recv_timeout(Duration::from_millis(20)).is_err());
        drop(held);
        assert!(push_rx.recv_timeout(Duration::from_secs(1)).unwrap() > 0.0);
        push.join().unwrap();

        let before = engine.encoder_epoch.load(Ordering::Acquire);
        let held = engine.capture_pipeline.lock().unwrap();
        let (reset_tx, reset_rx) = std::sync::mpsc::channel();
        let resetting = engine.clone();
        let reset = std::thread::spawn(move || {
            let _ = reset_tx.send(resetting.reset_encoder_history());
        });
        assert!(reset_rx.recv_timeout(Duration::from_millis(20)).is_err());
        assert_eq!(engine.encoder_epoch.load(Ordering::Acquire), before);
        drop(held);
        assert_eq!(
            reset_rx.recv_timeout(Duration::from_secs(1)).unwrap(),
            before + 1
        );
        reset.join().unwrap();
    }

    #[test]
    fn poll_signal_empty_when_idle() {
        let e = Engine::new();
        assert!(e.poll_signal().is_none());
    }

    #[test]
    fn mobile_diagnostics_are_opt_in_immediate_then_two_second_cadenced() {
        let e = Engine::new();
        assert!(e.poll_signal().is_none());

        e.control(r#"{"op":"set-diagnostics","enabled":true}"#);
        let first = e.poll_signal().expect("immediate diagnostics baseline");
        let value: serde_json::Value = serde_json::from_str(&first).unwrap();
        assert_eq!(value["op"], "mobile-stats");
        assert!(value["media_receive"].is_object());
        assert!(value["network_paths"].is_array());
        assert!(value["encoder_bitrate"].as_i64().unwrap() > 0);
        e.ack_signal();

        // Re-sending the same state must not bypass the cadence.
        e.control(r#"{"op":"set-diagnostics","enabled":true}"#);
        assert!(e.poll_signal().is_none());
        e.control(r#"{"op":"set-diagnostics","enabled":false}"#);
        assert!(e.poll_signal().is_none());
        e.control(r#"{"op":"set-diagnostics","enabled":true}"#);
        assert!(
            e.poll_signal().is_some(),
            "re-enable emits a fresh baseline"
        );
    }

    #[test]
    fn mobile_diagnostics_payload_drops_peer_candidate_and_address_identifiers() {
        let receive = crate::codec::MediaReceiveSnapshot {
            active_peers: 1,
            sequence_gaps: 3,
            local_media_gap_frames: 4,
            dred_frames: 2,
            rtp_jitter_ms_max: 27.5,
            latency_catchup_drops: 5,
            ..Default::default()
        };
        let path = PeerNetworkPathSnapshot {
            peer_id: "private-peer-192.168.1.20".to_string(),
            generation: 9,
            candidate_pair_id: "candidate-pair-203.0.113.5".to_string(),
            candidate_state: "succeeded".to_string(),
            local_candidate_type: "relay".to_string(),
            remote_candidate_type: "srflx".to_string(),
            relay: true,
            ice_connection_state: "connected".to_string(),
            local_candidate_protocol: "udp".to_string(),
            remote_candidate_protocol: "udp".to_string(),
            selected_pair_changes: 2,
            current_rtt_ms: 212.5,
            bandwidth_estimate_valid: true,
            available_outgoing_bitrate: 64_000.0,
            available_incoming_bitrate: 72_000.0,
            remote_packets_received: 123,
            remote_packets_lost: 4,
            remote_fraction_lost: 0.03,
            remote_report_rtt_ms: 210.0,
            remote_rtt_measurements: 8,
        };
        let json = mobile_diagnostics_json(
            receive,
            vec![path],
            crate::codec::EncoderPolicySnapshot::default(),
            crate::proto::NativeStatsSnapshot {
                capture_media_gap_frames: 7,
                opus_gap_placeholders: 1,
                opus_discontinuity_resets: 1,
                ..Default::default()
            },
        );
        assert!(!json.contains("peer_id"));
        assert!(!json.contains("candidate_pair_id"));
        assert!(!json.contains("192.168.1.20"));
        assert!(!json.contains("203.0.113.5"));
        let value: serde_json::Value = serde_json::from_str(&json).unwrap();
        assert_eq!(value["capture_media_gap_frames"], 7);
        assert_eq!(
            value["network_paths"][0]["ice_connection_state"],
            "connected"
        );
        assert_eq!(value["network_paths"][0]["local_candidate_protocol"], "udp");
        assert_eq!(value["network_paths"][0]["selected_pair_changes"], 2);
        assert_eq!(value["opus_gap_placeholders"], 1);
        assert_eq!(value["media_receive"]["local_media_gap_frames"], 4);
        assert_eq!(value["opus_discontinuity_resets"], 1);
        assert_eq!(value["network_paths"][0]["relay"], true);
        assert_eq!(value["network_paths"][0]["current_rtt_ms"], 212.5);
        assert_eq!(value["media_receive"]["sequence_gaps"], 3);
        assert_eq!(value["media_receive"]["dred_frames"], 2);
        assert_eq!(value["media_receive"]["latency_catchup_drops"], 5);
    }

    #[test]
    fn runtime_input_and_synthetic_controls_emit_bounded_level_telemetry() {
        let e = Engine::new();
        e.control(r#"{"op":"start"}"#);
        e.control(
            r#"{"op":"set-dsp","aec":false,"agc":false,"ns":false,"ns_very_high":false,"hpf":false}"#,
        );
        e.control(
            r#"{"op":"set-input","gain":2.0,"vad_threshold":0.0001,"noise_gate_threshold":0.003}"#,
        );
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

    #[test]
    fn android_forces_native_apm_features_off_even_if_control_requests_them() {
        let effective = android_dsp_config(DspConfig {
            aec: true,
            agc: true,
            ns: true,
            ns_very_high: true,
            hpf: true,
        });
        assert_eq!(
            effective,
            DspConfig {
                aec: false,
                agc: false,
                ns: false,
                ns_very_high: false,
                hpf: false,
            }
        );
    }
}

#[derive(Default)]
struct MobileDiagnosticsState {
    enabled: bool,
    next_emit: Option<Instant>,
}

#[derive(serde::Serialize)]
struct MobileNetworkPathStats {
    candidate_state: String,
    local_candidate_type: String,
    remote_candidate_type: String,
    relay: bool,
    ice_connection_state: String,
    local_candidate_protocol: String,
    remote_candidate_protocol: String,
    selected_pair_changes: u64,
    current_rtt_ms: f64,
    bandwidth_estimate_valid: bool,
    available_outgoing_bitrate: f64,
    available_incoming_bitrate: f64,
    remote_packets_received: u64,
    remote_packets_lost: i64,
    remote_fraction_lost: f64,
    remote_report_rtt_ms: f64,
    remote_rtt_measurements: u64,
}

#[derive(serde::Serialize)]
struct MobileDiagnosticsMsg {
    op: &'static str,
    media_receive: MediaReceiveStats,
    network_paths: Vec<MobileNetworkPathStats>,
    encoder_packet_loss_percent: u8,
    encoder_bitrate: i32,
    encoder_policy_generation: u64,
    rtp_tx_queue_dropped: u64,
    rtp_tx_stale_epoch_dropped: u64,
    rtp_tx_write_timeouts: u64,
    rtp_tx_queue_depth_max: u64,
    capture_media_gap_frames: u64,
    opus_gap_placeholders: u64,
    opus_discontinuity_resets: u64,
}

fn mobile_diagnostics_json(
    receive: crate::codec::MediaReceiveSnapshot,
    paths: Vec<PeerNetworkPathSnapshot>,
    encoder: crate::codec::EncoderPolicySnapshot,
    transport: crate::proto::NativeStatsSnapshot,
) -> String {
    let media_receive = MediaReceiveStats {
        active_peers: receive.active_peers,
        ingress_queue_overflow: receive.ingress_queue_overflow,
        ingress_queue_depth_current: receive.ingress_queue_depth_current,
        ingress_queue_depth_max: receive.ingress_queue_depth_max,
        ingress_peer_queue_depth_max: receive.ingress_peer_queue_depth_max,
        sequence_gaps: receive.sequence_gaps,
        local_media_gap_frames: receive.local_media_gap_frames,
        reordered_recovered: receive.reordered_recovered,
        late_drops: receive.late_drops,
        duplicate_drops: receive.duplicate_drops,
        encoded_overflow_drops: receive.encoded_overflow_drops,
        latency_catchup_drops: receive.latency_catchup_drops,
        deadline_losses: receive.deadline_losses,
        dred_frames: receive.dred_frames,
        fec_frames: receive.fec_frames,
        plc_frames: receive.plc_frames,
        decoder_resets: receive.decoder_resets,
        talkspurt_resets: receive.talkspurt_resets,
        underruns: receive.underruns,
        rebuffers: receive.rebuffers,
        target_frames_max: receive.target_frames_max,
        target_frames_current_max: receive.target_frames_current_max,
        depth_frames_max: receive.depth_frames_max,
        depth_frames_current: receive.depth_frames_current,
        rtp_jitter_ms_max: receive.rtp_jitter_ms_max,
    };
    // Drop peer ids, candidate-pair ids, addresses, and SDP identifiers before crossing FFI.
    let network_paths = paths
        .into_iter()
        .map(|path| MobileNetworkPathStats {
            candidate_state: path.candidate_state,
            local_candidate_type: path.local_candidate_type,
            remote_candidate_type: path.remote_candidate_type,
            relay: path.relay,
            ice_connection_state: path.ice_connection_state,
            local_candidate_protocol: path.local_candidate_protocol,
            remote_candidate_protocol: path.remote_candidate_protocol,
            selected_pair_changes: path.selected_pair_changes,
            current_rtt_ms: path.current_rtt_ms,
            bandwidth_estimate_valid: path.bandwidth_estimate_valid,
            available_outgoing_bitrate: path.available_outgoing_bitrate,
            available_incoming_bitrate: path.available_incoming_bitrate,
            remote_packets_received: path.remote_packets_received,
            remote_packets_lost: path.remote_packets_lost,
            remote_fraction_lost: path.remote_fraction_lost,
            remote_report_rtt_ms: path.remote_report_rtt_ms,
            remote_rtt_measurements: path.remote_rtt_measurements,
        })
        .collect();
    serde_json::to_string(&MobileDiagnosticsMsg {
        op: "mobile-stats",
        media_receive,
        network_paths,
        encoder_packet_loss_percent: encoder.packet_loss_percent,
        encoder_bitrate: encoder.bitrate,
        encoder_policy_generation: encoder.generation,
        rtp_tx_queue_dropped: transport.rtp_tx_queue_dropped,
        rtp_tx_stale_epoch_dropped: transport.rtp_tx_stale_epoch_dropped,
        rtp_tx_write_timeouts: transport.rtp_tx_write_timeouts,
        rtp_tx_queue_depth_max: transport.rtp_tx_queue_depth_max,
        capture_media_gap_frames: transport.capture_media_gap_frames,
        opus_gap_placeholders: transport.opus_gap_placeholders,
        opus_discontinuity_resets: transport.opus_discontinuity_resets,
    })
    .expect("mobile diagnostics serialize")
}
