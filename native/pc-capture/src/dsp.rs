use apm_sys::Apm;
use std::path::PathBuf;

#[derive(Clone, Copy)]
pub struct DspConfig {
    pub aec: bool,
    pub agc: bool,
    pub ns: bool,
    pub hpf: bool,
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

        eprintln!(
            "pc-capture: dsp set apm={} webrtc-ns={} automatic-gain=false requested-agc={}",
            self.apm.is_some(),
            cfg.ns && self.apm.is_some(),
            cfg.agc,
        );
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
    ) -> i32 {
        let applied_delay_ms = self
            .aec_stream_delay
            .update(measured_delay_ms, timing_complete);
        if let Some(apm) = self.apm.as_mut() {
            // WebRTC consumes the stream-delay marker on each 10 ms ProcessStream call. The APM
            // wrapper therefore reapplies this measured value to both halves of our 20 ms frame.
            apm.process_capture_with_stream_delay(mic, applied_delay_ms);
        }
        applied_delay_ms
    }

    #[allow(dead_code)]
    pub fn capture(&mut self, mic: &mut [f32]) {
        self.capture_with_stream_delay(mic, 0, false);
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
}
