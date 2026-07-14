use apm_sys::Apm;
use std::path::PathBuf;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DspConfig {
    pub aec: bool,
    pub agc: bool,
    pub ns: bool,
    pub hpf: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DspStatus {
    pub config_generation: u64,
    pub requested: DspConfig,
    pub apm_loaded: bool,
    pub config_fully_applied: bool,
    pub applied_aec: bool,
    pub applied_agc: bool,
    pub applied_ns: bool,
    pub applied_hpf: bool,
}

impl DspStatus {
    fn from_state(config_generation: u64, requested: DspConfig, apm_loaded: bool) -> Self {
        // Automatic gain remains deliberately disabled in the native WebRTC APM. Keep the
        // requested and applied values separate so diagnostics never claim AGC is active merely
        // because an older managed client requested it.
        let applied_aec = apm_loaded && requested.aec;
        let applied_agc = false;
        let applied_ns = apm_loaded && requested.ns;
        let applied_hpf = apm_loaded && requested.hpf;
        let config_fully_applied = requested.aec == applied_aec
            && requested.agc == applied_agc
            && requested.ns == applied_ns
            && requested.hpf == applied_hpf;
        Self {
            config_generation,
            requested,
            apm_loaded,
            config_fully_applied,
            applied_aec,
            applied_agc,
            applied_ns,
            applied_hpf,
        }
    }
}

impl Default for DspConfig {
    fn default() -> Self {
        DspConfig {
            aec: true,
            agc: false,
            ns: true,
            hpf: true,
        }
    }
}

const MAX_AEC_STREAM_DELAY_MS: i32 = 500;
const MAX_AEC_DELAY_STEP_PER_FRAME_MS: i32 = 5;

#[derive(Default)]
struct AecStreamDelay {
    current_ms: Option<i32>,
    has_complete_measurement: bool,
}

impl AecStreamDelay {
    fn update(&mut self, measured_ms: i32, timing_complete: bool) -> i32 {
        let measured_ms = measured_ms.clamp(0, MAX_AEC_STREAM_DELAY_MS);
        let next_ms = match (
            self.current_ms,
            timing_complete,
            self.has_complete_measurement,
        ) {
            (None, _, _) => measured_ms,
            // Replace the conservative startup value as soon as the first complete hardware and
            // queue measurement exists. This is the transition that used to need mute/unmute.
            (Some(_), true, false) => measured_ms,
            // A transiently incomplete sample must not pull an already-calibrated AEC back toward
            // the startup fallback.
            (Some(current_ms), false, true) => current_ms,
            (Some(current_ms), _, _) => {
                current_ms
                    + (measured_ms - current_ms).clamp(
                        -MAX_AEC_DELAY_STEP_PER_FRAME_MS,
                        MAX_AEC_DELAY_STEP_PER_FRAME_MS,
                    )
            }
        };
        self.has_complete_measurement |= timing_complete;
        self.current_ms = Some(next_ms);
        next_ms
    }
}

pub struct Dsp {
    apm: Option<Apm>,
    mono: Vec<f32>,
    aec_stream_delay: AecStreamDelay,
    playback_timing_epoch: Option<u64>,
    config: DspConfig,
    config_generation: u64,
}

#[cfg(all(windows, target_arch = "x86_64"))]
const APM_LIB: &str = "webrtc-apm.x64.dll";
#[cfg(all(windows, target_arch = "x86"))]
const APM_LIB: &str = "webrtc-apm.x86.dll";
#[cfg(target_os = "linux")]
const APM_LIB: &str = "libwebrtc-apm.so";
#[cfg(target_os = "macos")]
const APM_LIB: &str = "libwebrtc-apm.dylib";

#[cfg(target_os = "android")]
const APM_LIB: &str = "libwebrtc-apm.so";

fn lib_path(name: &str) -> String {
    let dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(PathBuf::from))
        .unwrap_or_else(|| PathBuf::from("."));
    dir.join(name).to_string_lossy().into_owned()
}

fn load_apm(cfg: &DspConfig) -> Option<Apm> {
    if !(cfg.aec || cfg.ns || cfg.hpf) {
        return None;
    }
    // Run AEC3, high WebRTC noise suppression, and the high-pass filter without automatic gain.
    match Apm::load(&lib_path(APM_LIB), cfg.aec, cfg.ns, false, cfg.hpf) {
        Ok(a) => Some(a),
        Err(e) => {
            eprintln!("pc-capture: apm unavailable, mic passthrough: {e}");
            None
        }
    }
}

impl Dsp {
    pub fn new(cfg: DspConfig) -> Dsp {
        let apm = load_apm(&cfg);
        let web_rtc_ns_enabled = cfg.ns && apm.is_some();
        eprintln!(
            "pc-capture: dsp apm={} webrtc-ns={} automatic-gain=false",
            apm.is_some(),
            web_rtc_ns_enabled,
        );
        Dsp {
            apm,
            mono: vec![0.0; crate::proto::AUDIO_OUT_FRAMES],
            aec_stream_delay: AecStreamDelay::default(),
            playback_timing_epoch: None,
            config: cfg,
            config_generation: 1,
        }
    }

    pub fn set(&mut self, cfg: DspConfig) {
        if cfg.aec || cfg.ns || cfg.hpf {
            if let Some(apm) = self.apm.as_mut() {
                if let Err(e) = apm.set_config(cfg.aec, cfg.ns, false, cfg.hpf) {
                    eprintln!("pc-capture: apm reconfigure failed, reloading: {e}");
                    self.apm = load_apm(&cfg);
                }
            } else {
                self.apm = load_apm(&cfg);
            }
        } else {
            self.apm = None;
        }

        self.config = cfg;
        self.config_generation = self.config_generation.saturating_add(1);

        eprintln!(
            "pc-capture: dsp set apm={} webrtc-ns={} automatic-gain=false requested-agc={}",
            self.apm.is_some(),
            cfg.ns && self.apm.is_some(),
            cfg.agc,
        );
    }

    pub fn status(&self) -> DspStatus {
        DspStatus::from_state(self.config_generation, self.config, self.apm.is_some())
    }

    pub fn begin_capture_generation(&mut self) {
        // A newly opened capture stream has a different hardware/queue path. Preserve the loaded
        // APM instance and its adaptive filter, but do not carry the previous stream's smoothed
        // delay marker into the first frame from the new generation.
        self.aec_stream_delay = AecStreamDelay::default();
    }

    pub fn far_end(&mut self, stereo: &[f32]) {
        let apm = match self.apm.as_mut() {
            Some(a) => a,
            None => return,
        };
        let frames = stereo.len() / 2;
        if self.mono.len() != frames {
            self.mono.resize(frames, 0.0);
        }
        for i in 0..frames {
            self.mono[i] = (stereo[i * 2] + stereo[i * 2 + 1]) * 0.5;
        }
        apm.analyze_reverse(&self.mono);
    }

    pub fn capture_with_stream_delay(
        &mut self,
        mic: &mut [f32],
        measured_delay_ms: i32,
        timing_complete: bool,
        playback_timing_epoch: u64,
    ) -> i32 {
        if self.playback_timing_epoch != Some(playback_timing_epoch) {
            // A new output stream has a different render hardware/queue path. Keep the adaptive
            // APM instance, but let this route's first complete measurement replace the old
            // route's delay immediately instead of walking toward it five milliseconds at a time.
            self.aec_stream_delay = AecStreamDelay::default();
            self.playback_timing_epoch = Some(playback_timing_epoch);
        }
        let applied_delay_ms = self
            .aec_stream_delay
            .update(measured_delay_ms, timing_complete);
        if let Some(apm) = self.apm.as_mut() {
            // WebRTC consumes the stream-delay marker on each 10 ms ProcessStream call. The APM
            // wrapper therefore reapplies the same end-to-end delay to both halves of our 20 ms
            // frame.
            apm.process_capture_with_stream_delay(mic, applied_delay_ms);
        }
        applied_delay_ms
    }

    #[allow(dead_code)]
    pub fn capture(&mut self, mic: &mut [f32]) {
        self.capture_with_stream_delay(mic, 0, false, 0);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn aec_delay_accepts_first_measurement_immediately() {
        let mut delay = AecStreamDelay::default();
        assert_eq!(delay.update(87, true), 87);
    }

    #[test]
    fn aec_delay_changes_are_bounded_per_audio_frame() {
        let mut delay = AecStreamDelay::default();
        assert_eq!(delay.update(50, true), 50);
        assert_eq!(delay.update(100, true), 55);
        assert_eq!(delay.update(100, true), 60);
        assert_eq!(delay.update(10, true), 55);
    }

    #[test]
    fn aec_delay_stays_within_webrtc_range() {
        let mut low = AecStreamDelay::default();
        let mut high = AecStreamDelay::default();
        assert_eq!(low.update(-100, true), 0);
        assert_eq!(high.update(900, true), MAX_AEC_STREAM_DELAY_MS);
    }

    #[test]
    fn first_complete_aec_measurement_replaces_startup_fallback() {
        let mut delay = AecStreamDelay::default();
        assert_eq!(delay.update(50, false), 50);
        assert_eq!(delay.update(112, true), 112);
        assert_eq!(delay.update(50, false), 112);
    }

    #[test]
    fn dsp_status_separates_requested_features_from_actual_apm_state() {
        let requested = DspConfig {
            aec: true,
            agc: true,
            ns: true,
            hpf: false,
        };
        let missing = DspStatus::from_state(7, requested, false);
        assert_eq!(missing.config_generation, 7);
        assert_eq!(missing.requested, requested);
        assert!(!missing.apm_loaded);
        assert!(!missing.config_fully_applied);
        assert!(!missing.applied_aec);
        assert!(!missing.applied_agc);
        assert!(!missing.applied_ns);
        assert!(!missing.applied_hpf);

        let loaded = DspStatus::from_state(8, requested, true);
        assert!(loaded.apm_loaded);
        assert!(loaded.applied_aec);
        assert!(loaded.applied_ns);
        assert!(!loaded.applied_agc);
        assert!(!loaded.config_fully_applied);
    }

    #[test]
    fn dsp_status_tracks_each_reconfiguration_generation() {
        let disabled = DspConfig {
            aec: false,
            agc: false,
            ns: false,
            hpf: false,
        };
        let mut dsp = Dsp::new(disabled);
        assert_eq!(dsp.status().config_generation, 1);
        assert!(dsp.status().config_fully_applied);

        dsp.set(disabled);
        let status = dsp.status();
        assert_eq!(status.config_generation, 2);
        assert_eq!(status.requested, disabled);
        assert!(!status.apm_loaded);
        assert!(status.config_fully_applied);
    }

    #[test]
    fn new_capture_generation_accepts_its_first_delay_measurement_immediately() {
        let disabled = DspConfig {
            aec: false,
            agc: false,
            ns: false,
            hpf: false,
        };
        let mut dsp = Dsp::new(disabled);
        let mut samples = [0.0; crate::proto::FRAME_SAMPLES];
        assert_eq!(dsp.capture_with_stream_delay(&mut samples, 50, true, 1), 50);
        assert_eq!(
            dsp.capture_with_stream_delay(&mut samples, 120, true, 1),
            55
        );

        dsp.begin_capture_generation();
        assert_eq!(
            dsp.capture_with_stream_delay(&mut samples, 120, true, 1),
            120
        );
    }

    #[test]
    fn new_playback_timing_epoch_accepts_its_first_delay_measurement_immediately() {
        let disabled = DspConfig {
            aec: false,
            agc: false,
            ns: false,
            hpf: false,
        };
        let mut dsp = Dsp::new(disabled);
        let mut samples = [0.0; crate::proto::FRAME_SAMPLES];
        assert_eq!(dsp.capture_with_stream_delay(&mut samples, 50, true, 1), 50);
        assert_eq!(
            dsp.capture_with_stream_delay(&mut samples, 120, true, 1),
            55
        );

        assert_eq!(
            dsp.capture_with_stream_delay(&mut samples, 120, true, 2),
            120
        );
    }
}
