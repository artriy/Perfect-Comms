use apm_sys::Apm;
use df_sys::Ns;
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
            agc: true,
            ns: true,
            hpf: true,
        }
    }
}

pub struct Dsp {
    apm: Option<Apm>,
    ns: Option<Ns>,
    mono: Vec<f32>,
}

#[cfg(all(windows, target_arch = "x86_64"))]
const APM_LIB: &str = "webrtc-apm.x64.dll";
#[cfg(all(windows, target_arch = "x86_64"))]
const DF_LIB: &str = "df.x64.dll";
#[cfg(all(windows, target_arch = "x86"))]
const APM_LIB: &str = "webrtc-apm.x86.dll";
#[cfg(all(windows, target_arch = "x86"))]
const DF_LIB: &str = "df.x86.dll";
#[cfg(target_os = "linux")]
const APM_LIB: &str = "libwebrtc-apm.so";
#[cfg(target_os = "linux")]
const DF_LIB: &str = "libdf.so";
#[cfg(target_os = "macos")]
const APM_LIB: &str = "libwebrtc-apm.dylib";
#[cfg(target_os = "macos")]
const DF_LIB: &str = "libdf.dylib";

fn lib_path(name: &str) -> String {
    let dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(PathBuf::from))
        .unwrap_or_else(|| PathBuf::from("."));
    dir.join(name).to_string_lossy().into_owned()
}

fn load_apm(cfg: &DspConfig) -> Option<Apm> {
    if !(cfg.aec || cfg.agc || cfg.hpf) {
        return None;
    }
    match Apm::load(&lib_path(APM_LIB), cfg.aec, cfg.agc, cfg.hpf) {
        Ok(a) => Some(a),
        Err(e) => {
            eprintln!("pc-capture: apm unavailable, mic passthrough: {e}");
            None
        }
    }
}

fn load_ns(cfg: &DspConfig) -> Option<Ns> {
    if !cfg.ns {
        return None;
    }
    match Ns::load(&lib_path(DF_LIB)) {
        Ok(n) => Some(n),
        Err(e) => {
            eprintln!("pc-capture: ns unavailable, skipping: {e}");
            None
        }
    }
}

impl Dsp {
    pub fn new(cfg: DspConfig) -> Dsp {
        let apm = load_apm(&cfg);
        let ns = load_ns(&cfg);
        eprintln!(
            "pc-capture: dsp aec/agc/hpf={} ns={}",
            apm.is_some(),
            ns.is_some()
        );
        Dsp {
            apm,
            ns,
            mono: vec![0.0; crate::proto::AUDIO_OUT_FRAMES],
        }
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

    pub fn capture(&mut self, mic: &mut [f32]) {
        if let Some(apm) = self.apm.as_mut() {
            apm.set_stream_delay_ms(0);
            apm.process_capture(mic);
        }
        if let Some(ns) = self.ns.as_mut() {
            ns.process(mic);
        }
    }
}
