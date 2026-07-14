use libloading::{Library, Symbol};
use std::os::raw::c_int;

#[repr(C)]
pub struct ApmHandle {
    _p: [u8; 0],
}
#[repr(C)]
pub struct ApmConfig {
    _p: [u8; 0],
}
#[repr(C)]
pub struct ApmStreamConfig {
    _p: [u8; 0],
}

type FnCreate = unsafe extern "C" fn() -> *mut ApmHandle;
type FnDestroy = unsafe extern "C" fn(*mut ApmHandle);
type FnConfigCreate = unsafe extern "C" fn() -> *mut ApmConfig;
type FnConfigDestroy = unsafe extern "C" fn(*mut ApmConfig);
type FnSetEcho = unsafe extern "C" fn(*mut ApmConfig, c_int, c_int);
type FnSetNs = unsafe extern "C" fn(*mut ApmConfig, c_int, c_int);
type FnSetGc1 = unsafe extern "C" fn(*mut ApmConfig, c_int, c_int, c_int, c_int, c_int);
type FnSetGc2 = unsafe extern "C" fn(*mut ApmConfig, c_int);
type FnSetHpf = unsafe extern "C" fn(*mut ApmConfig, c_int);
type FnApply = unsafe extern "C" fn(*const ApmHandle, *const ApmConfig) -> c_int;
type FnInit = unsafe extern "C" fn(*const ApmHandle) -> c_int;
type FnScCreate = unsafe extern "C" fn(c_int, usize) -> *mut ApmStreamConfig;
type FnScDestroy = unsafe extern "C" fn(*mut ApmStreamConfig);
type FnProcess = unsafe extern "C" fn(
    *const ApmHandle,
    *const *const f32,
    *const ApmStreamConfig,
    *const ApmStreamConfig,
    *const *mut f32,
) -> c_int;
type FnAnalyze =
    unsafe extern "C" fn(*const ApmHandle, *const *const f32, *const ApmStreamConfig) -> c_int;
type FnSetDelay = unsafe extern "C" fn(*const ApmHandle, c_int);
type FnFrameSize = unsafe extern "C" fn(c_int) -> usize;

struct Api {
    _lib: Library,
    create: FnCreate,
    destroy: FnDestroy,
    config_create: FnConfigCreate,
    config_destroy: FnConfigDestroy,
    set_echo: FnSetEcho,
    set_ns: FnSetNs,
    set_gc1: FnSetGc1,
    set_gc2: FnSetGc2,
    set_hpf: FnSetHpf,
    apply: FnApply,
    init: FnInit,
    sc_create: FnScCreate,
    sc_destroy: FnScDestroy,
    process: FnProcess,
    analyze: FnAnalyze,
    set_delay: FnSetDelay,
    frame_size: FnFrameSize,
}

impl Api {
    unsafe fn load(path: &str) -> Result<Api, String> {
        let lib = Library::new(path).map_err(|e| format!("load:{e}"))?;
        macro_rules! sym {
            ($n:expr) => {{
                let s: Symbol<_> = lib
                    .get($n)
                    .map_err(|e| format!("sym {}:{}", String::from_utf8_lossy($n), e))?;
                *s
            }};
        }
        Ok(Api {
            create: sym!(b"webrtc_apm_create"),
            destroy: sym!(b"webrtc_apm_destroy"),
            config_create: sym!(b"webrtc_apm_config_create"),
            config_destroy: sym!(b"webrtc_apm_config_destroy"),
            set_echo: sym!(b"webrtc_apm_config_set_echo_canceller"),
            set_ns: sym!(b"webrtc_apm_config_set_noise_suppression"),
            set_gc1: sym!(b"webrtc_apm_config_set_gain_controller1"),
            set_gc2: sym!(b"webrtc_apm_config_set_gain_controller2"),
            set_hpf: sym!(b"webrtc_apm_config_set_high_pass_filter"),
            apply: sym!(b"webrtc_apm_apply_config"),
            init: sym!(b"webrtc_apm_initialize"),
            sc_create: sym!(b"webrtc_apm_stream_config_create"),
            sc_destroy: sym!(b"webrtc_apm_stream_config_destroy"),
            process: sym!(b"webrtc_apm_process_stream"),
            analyze: sym!(b"webrtc_apm_analyze_reverse_stream"),
            set_delay: sym!(b"webrtc_apm_set_stream_delay_ms"),
            frame_size: sym!(b"webrtc_apm_get_frame_size"),
            _lib: lib,
        })
    }
}

const RATE: c_int = 48000;
const FRAME: usize = 960;
const MAX_STREAM_DELAY_MS: i32 = 500;
// Chromium's getUserMedia voice pipeline uses WebRTC noise suppression at kHigh.
const NS_LEVEL_HIGH: c_int = 2;

fn sanitize_stream_delay_ms(delay_ms: i32) -> i32 {
    delay_ms.clamp(0, MAX_STREAM_DELAY_MS)
}

pub struct Apm {
    api: Api,
    a: *mut ApmHandle,
    sc: *mut ApmStreamConfig,
    chunk: usize,
    out: Vec<f32>,
}

unsafe impl Send for Apm {}

impl Apm {
    pub fn load(
        lib_path: &str,
        echo: bool,
        noise_suppression: bool,
        agc2: bool,
        hpf: bool,
    ) -> Result<Apm, String> {
        unsafe {
            let api = Api::load(lib_path)?;
            let a = (api.create)();
            if a.is_null() {
                return Err("apm-create".into());
            }
            let chunk = (api.frame_size)(RATE);
            if chunk == 0 || FRAME % chunk != 0 {
                (api.destroy)(a);
                return Err(format!("chunk:{chunk}"));
            }
            let mut me = Apm {
                api,
                a,
                sc: std::ptr::null_mut(),
                chunk,
                out: vec![0.0; chunk],
            };
            me.apply(echo, noise_suppression, agc2, hpf)?;
            me.sc = (me.api.sc_create)(RATE, 1);
            if me.sc.is_null() {
                return Err("stream-config".into());
            }
            Ok(me)
        }
    }

    fn apply(
        &mut self,
        echo: bool,
        noise_suppression: bool,
        agc2: bool,
        hpf: bool,
    ) -> Result<(), String> {
        unsafe {
            let c = (self.api.config_create)();
            if c.is_null() {
                return Err("config-create".into());
            }
            (self.api.set_echo)(c, echo as c_int, 0);
            (self.api.set_ns)(c, noise_suppression as c_int, NS_LEVEL_HIGH);
            (self.api.set_gc1)(c, 0, 0, 0, 0, 0);
            (self.api.set_gc2)(c, agc2 as c_int);
            (self.api.set_hpf)(c, hpf as c_int);
            let e = (self.api.apply)(self.a, c);
            (self.api.config_destroy)(c);
            if e != 0 {
                return Err(format!("apply:{e}"));
            }
            let i = (self.api.init)(self.a);
            if i != 0 {
                return Err(format!("init:{i}"));
            }
            Ok(())
        }
    }

    pub fn set_config(
        &mut self,
        echo: bool,
        noise_suppression: bool,
        agc2: bool,
        hpf: bool,
    ) -> Result<(), String> {
        self.apply(echo, noise_suppression, agc2, hpf)
    }

    pub fn chunk(&self) -> usize {
        self.chunk
    }

    pub fn analyze_reverse(&mut self, frame: &[f32]) {
        unsafe {
            let mut off = 0;
            while off + self.chunk <= frame.len() {
                let p = frame.as_ptr().add(off);
                let pp = &p as *const *const f32;
                (self.api.analyze)(self.a, pp, self.sc);
                off += self.chunk;
            }
        }
    }

    pub fn process_capture(&mut self, frame: &mut [f32]) {
        self.process_capture_with_stream_delay(frame, 0);
    }

    /// Processes capture audio while supplying the render-to-capture delay required by WebRTC
    /// APM. A 20 ms PerfectComms frame contains two 10 ms APM chunks, and APM clears its
    /// `was_stream_delay_set` flag after every ProcessStream call. Set the delay before every
    /// internal chunk rather than once per PerfectComms frame.
    pub fn process_capture_with_stream_delay(&mut self, frame: &mut [f32], delay_ms: i32) {
        let delay_ms = sanitize_stream_delay_ms(delay_ms);
        unsafe {
            let mut off = 0;
            while off + self.chunk <= frame.len() {
                let ip = frame.as_ptr().add(off);
                let op = self.out.as_mut_ptr();
                let ipp = &ip as *const *const f32;
                let opp = &op as *const *mut f32;
                // The second capture chunk is 10 ms newer, but its corresponding render chunk is
                // also 10 ms deeper in the same analyzed/output frame. Those offsets cancel, so
                // WebRTC receives the same end-to-end stream delay for both chunks.
                (self.api.set_delay)(self.a, delay_ms);
                (self.api.process)(self.a, ipp, self.sc, self.sc, opp);
                std::ptr::copy_nonoverlapping(
                    self.out.as_ptr(),
                    frame.as_mut_ptr().add(off),
                    self.chunk,
                );
                off += self.chunk;
            }
        }
    }

    pub fn set_stream_delay_ms(&mut self, ms: i32) {
        unsafe {
            (self.api.set_delay)(self.a, sanitize_stream_delay_ms(ms));
        }
    }
}

impl Drop for Apm {
    fn drop(&mut self) {
        unsafe {
            if !self.sc.is_null() {
                (self.api.sc_destroy)(self.sc);
            }
            if !self.a.is_null() {
                (self.api.destroy)(self.a);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::{sanitize_stream_delay_ms, Apm, FRAME};

    #[test]
    fn stream_delay_is_bounded_to_webrtc_range() {
        assert_eq!(sanitize_stream_delay_ms(-1), 0);
        assert_eq!(sanitize_stream_delay_ms(73), 73);
        assert_eq!(sanitize_stream_delay_ms(900), 500);
    }

    #[test]
    #[ignore]
    fn loads_and_processes_a_frame() {
        let path =
            std::env::var("APM_LIB").expect("set APM_LIB to the webrtc-apm shared library path");
        let mut apm = Apm::load(&path, true, true, false, true).expect("load");
        let mut frame = vec![0.0f32; 960];
        apm.analyze_reverse(&frame);
        apm.process_capture_with_stream_delay(&mut frame, 73);
        assert_eq!(frame.len(), 960);
    }

    #[test]
    #[ignore]
    fn high_noise_suppression_attenuates_stationary_noise() {
        let path =
            std::env::var("APM_LIB").expect("set APM_LIB to the webrtc-apm shared library path");
        let mut bypass = Apm::load(&path, false, false, false, false).expect("load bypass");
        let mut suppressed = Apm::load(&path, false, true, false, false).expect("load suppressed");
        let mut random_state = 0x1234_5678u32;
        let mut bypass_energy = 0.0f64;
        let mut suppressed_energy = 0.0f64;
        let mut measured_samples = 0usize;

        for frame_index in 0..200 {
            let mut input = vec![0.0f32; FRAME];
            for sample in &mut input {
                random_state = random_state
                    .wrapping_mul(1_664_525)
                    .wrapping_add(1_013_904_223);
                let unit = (random_state >> 8) as f32 / 16_777_215.0;
                *sample = (unit * 2.0 - 1.0) * 0.03;
            }

            let mut bypass_frame = input.clone();
            let mut suppressed_frame = input;
            bypass.process_capture(&mut bypass_frame);
            suppressed.process_capture(&mut suppressed_frame);

            if frame_index >= 150 {
                bypass_energy += bypass_frame
                    .iter()
                    .map(|sample| f64::from(*sample) * f64::from(*sample))
                    .sum::<f64>();
                suppressed_energy += suppressed_frame
                    .iter()
                    .map(|sample| f64::from(*sample) * f64::from(*sample))
                    .sum::<f64>();
                measured_samples += FRAME;
            }
        }

        let bypass_rms = (bypass_energy / measured_samples as f64).sqrt();
        let suppressed_rms = (suppressed_energy / measured_samples as f64).sqrt();
        assert!(
            suppressed_rms < bypass_rms * 0.85,
            "suppression RMS {suppressed_rms:.6} was not below bypass RMS {bypass_rms:.6}"
        );
    }
}
