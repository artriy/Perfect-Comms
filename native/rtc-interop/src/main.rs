use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::Duration;

use anyhow::Result;
use bytes::Bytes;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};

use webrtc::api::interceptor_registry::register_default_interceptors;
use webrtc::api::media_engine::{MediaEngine, MIME_TYPE_OPUS};
use webrtc::api::APIBuilder;
use webrtc::ice_transport::ice_connection_state::RTCIceConnectionState;
use webrtc::interceptor::registry::Registry;
use webrtc::media::Sample;
use webrtc::peer_connection::configuration::RTCConfiguration;
use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;
use webrtc::peer_connection::sdp::session_description::RTCSessionDescription;
use webrtc::rtp_transceiver::rtp_codec::{
    RTCRtpCodecCapability, RTCRtpCodecParameters, RTPCodecType,
};
use webrtc::track::track_local::track_local_static_sample::TrackLocalStaticSample;
use webrtc::track::track_local::TrackLocal;

fn opus_capability() -> RTCRtpCodecCapability {
    RTCRtpCodecCapability {
        mime_type: MIME_TYPE_OPUS.to_owned(),
        clock_rate: 48000,
        channels: 2,
        sdp_fmtp_line: "minptime=10;useinbandfec=1".to_owned(),
        rtcp_feedback: vec![],
    }
}

fn encode_tone_frame() -> Bytes {
    let frame = 960usize;
    let mut pcm = vec![0i16; frame * 2];
    let freq = 440.0f32;
    for i in 0..frame {
        let s = ((i as f32) * 2.0 * std::f32::consts::PI * freq / 48000.0).sin();
        let v = (s * 8000.0) as i16;
        pcm[i * 2] = v;
        pcm[i * 2 + 1] = v;
    }
    match audiopus::coder::Encoder::new(
        audiopus::SampleRate::Hz48000,
        audiopus::Channels::Stereo,
        audiopus::Application::Audio,
    ) {
        Ok(enc) => {
            let mut out = vec![0u8; 4000];
            match enc.encode(&pcm, &mut out) {
                Ok(n) => {
                    out.truncate(n);
                    eprintln!("[offer] opus tone frame encoded: {n} bytes");
                    Bytes::from(out)
                }
                Err(e) => {
                    eprintln!("[offer] opus encode failed ({e}); using silence payload");
                    Bytes::from_static(&[0xf8, 0xff, 0xfe])
                }
            }
        }
        Err(e) => {
            eprintln!("[offer] opus encoder init failed ({e}); using silence payload");
            Bytes::from_static(&[0xf8, 0xff, 0xfe])
        }
    }
}

#[tokio::main]
async fn main() -> Result<()> {
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

    let api = APIBuilder::new()
        .with_media_engine(m)
        .with_interceptor_registry(registry)
        .build();

    let pc = Arc::new(api.new_peer_connection(RTCConfiguration::default()).await?);

    let track = Arc::new(TrackLocalStaticSample::new(
        opus_capability(),
        "audio".to_owned(),
        "perfectcomms-rtc-interop".to_owned(),
    ));

    let rtp_sender = pc
        .add_track(Arc::clone(&track) as Arc<dyn TrackLocal + Send + Sync>)
        .await?;

    tokio::spawn(async move {
        let mut rtcp_buf = vec![0u8; 1500];
        while rtp_sender.read(&mut rtcp_buf).await.is_ok() {}
    });

    let track_writer = Arc::clone(&track);
    tokio::spawn(async move {
        let payload = encode_tone_frame();
        let mut ticker = tokio::time::interval(Duration::from_millis(20));
        loop {
            ticker.tick().await;
            let sample = Sample {
                data: payload.clone(),
                duration: Duration::from_millis(20),
                ..Default::default()
            };
            let _ = track_writer.write_sample(&sample).await;
        }
    });

    pc.on_ice_candidate(Box::new(move |c| {
        Box::pin(async move {
            if let Some(c) = c {
                match c.to_json() {
                    Ok(ci) => eprintln!(
                        "[offer] local-candidate {}",
                        serde_json::to_string(&ci).unwrap_or_default()
                    ),
                    Err(e) => eprintln!("[offer] candidate to_json error: {e}"),
                }
            }
        })
    }));

    let recv_count = Arc::new(AtomicU64::new(0));
    let rc_track = Arc::clone(&recv_count);
    pc.on_track(Box::new(move |track, _receiver, _transceiver| {
        let rc = Arc::clone(&rc_track);
        Box::pin(async move {
            eprintln!(
                "[offer] on_track ssrc={} payload_type={}",
                track.ssrc(),
                track.payload_type()
            );
            tokio::spawn(async move {
                while let Ok((_pkt, _attr)) = track.read_rtp().await {
                    let n = rc.fetch_add(1, Ordering::SeqCst) + 1;
                    if n == 1 || n % 50 == 0 {
                        eprintln!("[offer] RTP packets received: {n}");
                    }
                }
            });
        })
    }));

    pc.on_ice_connection_state_change(Box::new(|s: RTCIceConnectionState| {
        Box::pin(async move {
            eprintln!("[offer] ICE connection state: {s}");
        })
    }));

    let (done_tx, mut done_rx) = tokio::sync::mpsc::channel::<()>(1);
    pc.on_peer_connection_state_change(Box::new(move |s: RTCPeerConnectionState| {
        let done_tx = done_tx.clone();
        Box::pin(async move {
            eprintln!("[offer] peer connection state: {s}");
            if s == RTCPeerConnectionState::Failed || s == RTCPeerConnectionState::Closed {
                let _ = done_tx.try_send(());
            }
        })
    }));

    let offer = pc.create_offer(None).await?;
    let mut gather_complete = pc.gathering_complete_promise().await;
    pc.set_local_description(offer).await?;
    let _ = gather_complete.recv().await;

    let local = pc
        .local_description()
        .await
        .ok_or_else(|| anyhow::anyhow!("no local description after gathering"))?;

    let mut stdout = tokio::io::stdout();
    stdout
        .write_all(serde_json::to_string(&local)?.as_bytes())
        .await?;
    stdout.write_all(b"\n").await?;
    stdout.flush().await?;
    eprintln!("[offer] offer SDP sent ({} bytes)", local.sdp.len());

    let mut reader = BufReader::new(tokio::io::stdin());
    let mut line = String::new();
    let n = reader.read_line(&mut line).await?;
    if n == 0 {
        anyhow::bail!("stdin closed before answer received");
    }
    let answer: RTCSessionDescription = serde_json::from_str(line.trim())?;
    eprintln!("[offer] answer SDP received ({} bytes)", answer.sdp.len());
    pc.set_remote_description(answer).await?;

    let deadline = tokio::time::sleep(Duration::from_secs(20));
    tokio::pin!(deadline);
    loop {
        tokio::select! {
            _ = &mut deadline => { eprintln!("[offer] deadline reached"); break; }
            _ = done_rx.recv() => { eprintln!("[offer] terminal connection state"); break; }
        }
    }

    let total = recv_count.load(Ordering::SeqCst);
    eprintln!(
        "[offer] FINAL state={} rtp_received={}",
        pc.connection_state(),
        total
    );
    pc.close().await?;
    Ok(())
}
