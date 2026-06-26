use std::collections::HashMap;
use std::sync::mpsc::{Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use bytes::Bytes;
use tokio::runtime::Runtime;

use webrtc::api::interceptor_registry::register_default_interceptors;
use webrtc::api::media_engine::{MediaEngine, MIME_TYPE_OPUS};
use webrtc::api::{APIBuilder, API};
use webrtc::ice_transport::ice_candidate::RTCIceCandidateInit;
use webrtc::interceptor::registry::Registry;
use webrtc::media::Sample;
use webrtc::peer_connection::configuration::RTCConfiguration;
use webrtc::peer_connection::sdp::session_description::RTCSessionDescription;
use webrtc::peer_connection::RTCPeerConnection;
use webrtc::rtp_transceiver::rtp_codec::{
    RTCRtpCodecCapability, RTCRtpCodecParameters, RTPCodecType,
};
use webrtc::track::track_local::track_local_static_sample::TrackLocalStaticSample;
use webrtc::track::track_local::TrackLocal;

#[derive(Debug, Clone)]
pub enum LocalSignal {
    Sdp {
        peer_id: String,
        sdp_type: String,
        sdp: String,
    },
    Candidate {
        peer_id: String,
        candidate: String,
    },
}

struct PeerHandle {
    pc: Arc<RTCPeerConnection>,
    track: Arc<TrackLocalStaticSample>,
}

pub struct RtcEngine {
    rt: Runtime,
    peers: Mutex<HashMap<String, PeerHandle>>,
    out_local_signal: Sender<LocalSignal>,
    recv_tx: Sender<(String, Vec<u8>)>,
    recv_rx: Mutex<Receiver<(String, Vec<u8>)>>,
}

fn opus_capability() -> RTCRtpCodecCapability {
    RTCRtpCodecCapability {
        mime_type: MIME_TYPE_OPUS.to_owned(),
        clock_rate: 48000,
        channels: 2,
        sdp_fmtp_line: "minptime=10;useinbandfec=1".to_owned(),
        rtcp_feedback: vec![],
    }
}

fn build_api() -> Result<API, webrtc::Error> {
    let mut m = MediaEngine::default();
    m.register_codec(
        RTCRtpCodecParameters {
            capability: opus_capability(),
            payload_type: 111,
            ..Default::default()
        },
        RTPCodecType::Audio,
    )?;
    let mut registry = Registry::new();
    registry = register_default_interceptors(registry, &mut m)?;
    Ok(APIBuilder::new()
        .with_media_engine(m)
        .with_interceptor_registry(registry)
        .build())
}

async fn create_peer(
    peer_id: String,
    offerer: bool,
    out_local_signal: Sender<LocalSignal>,
    recv_tx: Sender<(String, Vec<u8>)>,
) -> Result<PeerHandle, webrtc::Error> {
    let api = build_api()?;
    let pc = Arc::new(api.new_peer_connection(RTCConfiguration::default()).await?);

    let track = Arc::new(TrackLocalStaticSample::new(
        opus_capability(),
        "audio".to_owned(),
        "perfectcomms".to_owned(),
    ));
    let rtp_sender = pc
        .add_track(Arc::clone(&track) as Arc<dyn TrackLocal + Send + Sync>)
        .await?;
    tokio::spawn(async move {
        let mut buf = vec![0u8; 1500];
        while rtp_sender.read(&mut buf).await.is_ok() {}
    });

    let cand_signal = out_local_signal.clone();
    let cand_peer = peer_id.clone();
    pc.on_ice_candidate(Box::new(move |c| {
        let signal = cand_signal.clone();
        let pid = cand_peer.clone();
        Box::pin(async move {
            if let Some(c) = c {
                if let Ok(init) = c.to_json() {
                    if let Ok(s) = serde_json::to_string(&init) {
                        let _ = signal.send(LocalSignal::Candidate {
                            peer_id: pid,
                            candidate: s,
                        });
                    }
                }
            }
        })
    }));

    let track_peer = peer_id.clone();
    pc.on_track(Box::new(move |track, _receiver, _transceiver| {
        let tx = recv_tx.clone();
        let pid = track_peer.clone();
        Box::pin(async move {
            tokio::spawn(async move {
                while let Ok((pkt, _attr)) = track.read_rtp().await {
                    if !pkt.payload.is_empty() {
                        let _ = tx.send((pid.clone(), pkt.payload.to_vec()));
                    }
                }
            });
        })
    }));

    if offerer {
        let offer = pc.create_offer(None).await?;
        pc.set_local_description(offer).await?;
        if let Some(local) = pc.local_description().await {
            let _ = out_local_signal.send(LocalSignal::Sdp {
                peer_id: peer_id.clone(),
                sdp_type: local.sdp_type.to_string(),
                sdp: local.sdp,
            });
        }
    }

    Ok(PeerHandle { pc, track })
}

#[allow(dead_code)]
impl RtcEngine {
    pub fn new(out_local_signal: Sender<LocalSignal>) -> RtcEngine {
        let rt = tokio::runtime::Builder::new_multi_thread()
            .enable_all()
            .build()
            .expect("rtc tokio runtime");
        let (recv_tx, recv_rx) = std::sync::mpsc::channel();
        RtcEngine {
            rt,
            peers: Mutex::new(HashMap::new()),
            out_local_signal,
            recv_tx,
            recv_rx: Mutex::new(recv_rx),
        }
    }

    pub fn add_peer(&self, peer_id: String) {
        if self.peers.lock().unwrap().contains_key(&peer_id) {
            return;
        }
        let signal = self.out_local_signal.clone();
        let recv_tx = self.recv_tx.clone();
        let pid = peer_id.clone();
        if let Ok(handle) = self.rt.block_on(create_peer(pid, true, signal, recv_tx)) {
            self.peers.lock().unwrap().insert(peer_id, handle);
        }
    }

    pub fn remove_peer(&self, peer_id: &str) {
        let handle = self.peers.lock().unwrap().remove(peer_id);
        if let Some(h) = handle {
            let _ = self.rt.block_on(async move { h.pc.close().await });
        }
    }

    pub fn set_remote_sdp(&self, peer_id: &str, sdp_type: &str, sdp: &str) {
        let existing = self
            .peers
            .lock()
            .unwrap()
            .get(peer_id)
            .map(|h| Arc::clone(&h.pc));
        let pc = match existing {
            Some(p) => p,
            None => {
                let signal = self.out_local_signal.clone();
                let recv_tx = self.recv_tx.clone();
                let pid = peer_id.to_string();
                match self.rt.block_on(create_peer(pid, false, signal, recv_tx)) {
                    Ok(h) => {
                        let pc = Arc::clone(&h.pc);
                        self.peers.lock().unwrap().insert(peer_id.to_string(), h);
                        pc
                    }
                    Err(_) => return,
                }
            }
        };

        let desc = match sdp_type {
            "offer" => RTCSessionDescription::offer(sdp.to_string()),
            "answer" => RTCSessionDescription::answer(sdp.to_string()),
            "pranswer" => RTCSessionDescription::pranswer(sdp.to_string()),
            _ => return,
        };
        let desc = match desc {
            Ok(d) => d,
            Err(_) => return,
        };
        let signal = self.out_local_signal.clone();
        let pid = peer_id.to_string();
        let is_offer = sdp_type == "offer";
        let _ = self.rt.block_on(async move {
            pc.set_remote_description(desc).await?;
            if is_offer {
                let answer = pc.create_answer(None).await?;
                pc.set_local_description(answer).await?;
                if let Some(local) = pc.local_description().await {
                    let _ = signal.send(LocalSignal::Sdp {
                        peer_id: pid,
                        sdp_type: local.sdp_type.to_string(),
                        sdp: local.sdp,
                    });
                }
            }
            Ok::<(), webrtc::Error>(())
        });
    }

    pub fn add_ice_candidate(&self, peer_id: &str, candidate: &str) {
        let pc = match self
            .peers
            .lock()
            .unwrap()
            .get(peer_id)
            .map(|h| Arc::clone(&h.pc))
        {
            Some(p) => p,
            None => return,
        };
        let init = match serde_json::from_str::<RTCIceCandidateInit>(candidate) {
            Ok(i) => i,
            Err(_) => RTCIceCandidateInit {
                candidate: candidate.to_string(),
                ..Default::default()
            },
        };
        let _ = self.rt.block_on(async move { pc.add_ice_candidate(init).await });
    }

    pub fn send_opus(&self, pkt: &[u8]) {
        let tracks: Vec<Arc<TrackLocalStaticSample>> = self
            .peers
            .lock()
            .unwrap()
            .values()
            .map(|h| Arc::clone(&h.track))
            .collect();
        if tracks.is_empty() {
            return;
        }
        let data = Bytes::copy_from_slice(pkt);
        self.rt.block_on(async move {
            for t in tracks {
                let sample = Sample {
                    data: data.clone(),
                    duration: Duration::from_millis(20),
                    ..Default::default()
                };
                let _ = t.write_sample(&sample).await;
            }
        });
    }

    pub fn recv(&self) -> Option<(String, Vec<u8>)> {
        self.recv_rx.lock().unwrap().try_recv().ok()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::mpsc::channel;
    use std::time::Instant;

    #[test]
    fn loopback_two_engines_exchange_opus() {
        let (a_tx, a_rx) = channel::<LocalSignal>();
        let (b_tx, b_rx) = channel::<LocalSignal>();
        let a = RtcEngine::new(a_tx);
        let b = RtcEngine::new(b_tx);

        a.add_peer("B".to_string());

        let payload: Vec<u8> = (0..80u8).map(|i| i ^ 0x5a).collect();
        let deadline = Instant::now() + Duration::from_secs(30);
        let mut received: Option<Vec<u8>> = None;
        let mut count = 0u32;

        while Instant::now() < deadline {
            while let Ok(sig) = a_rx.try_recv() {
                match sig {
                    LocalSignal::Sdp { sdp_type, sdp, .. } => b.set_remote_sdp("A", &sdp_type, &sdp),
                    LocalSignal::Candidate { candidate, .. } => b.add_ice_candidate("A", &candidate),
                }
            }
            while let Ok(sig) = b_rx.try_recv() {
                match sig {
                    LocalSignal::Sdp { sdp_type, sdp, .. } => a.set_remote_sdp("B", &sdp_type, &sdp),
                    LocalSignal::Candidate { candidate, .. } => a.add_ice_candidate("B", &candidate),
                }
            }
            a.send_opus(&payload);
            while let Some((_pid, pkt)) = b.recv() {
                count += 1;
                if received.is_none() {
                    received = Some(pkt);
                }
            }
            if count >= 10 {
                break;
            }
            std::thread::sleep(Duration::from_millis(20));
        }

        assert!(
            count >= 1,
            "peer B never received any opus packets over webrtc"
        );
        assert_eq!(
            received.as_deref(),
            Some(payload.as_slice()),
            "received opus payload mismatch"
        );
    }
}
