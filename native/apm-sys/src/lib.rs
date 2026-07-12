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

pub struct Apm {
    api: Api,
    a: *mut ApmHandle,
    sc: *mut ApmStreamConfig,
    chunk: usize,
    out: Vec<f32>,
}

unsafe impl Send for Apm {}

impl Apm {
    pub fn load(lib_path: &str, echo: bool, agc2: bool, hpf: bool) -> Result<Apm, String> {
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
            me.apply(echo, agc2, hpf)?;
            me.sc = (me.api.sc_create)(RATE, 1);
            if me.sc.is_null() {
                return Err("stream-config".into());
            }
            Ok(me)
        }
    }

    fn apply(&mut self, echo: bool, agc2: bool, hpf: bool) -> Result<(), String> {
        unsafe {
            let c = (self.api.config_create)();
            if c.is_null() {
                return Err("config-create".into());
            }
            (self.api.set_echo)(c, echo as c_int, 0);
            (self.api.set_ns)(c, 0, 0);
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

    pub fn set_config(&mut self, echo: bool, agc2: bool, hpf: bool) -> Result<(), String> {
        self.apply(echo, agc2, hpf)
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
        unsafe {
            let mut off = 0;
            while off + self.chunk <= frame.len() {
                let ip = frame.as_ptr().add(off);
                let op = self.out.as_mut_ptr();
                let ipp = &ip as *const *const f32;
                let opp = &op as *const *mut f32;
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
            (self.api.set_delay)(self.a, ms.max(0));
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
    use super::Apm;

    #[test]
    #[ignore]
    fn loads_and_processes_a_frame() {
        let path =
            std::env::var("APM_LIB").expect("set APM_LIB to the webrtc-apm shared library path");
        let mut apm = Apm::load(&path, true, true, true).expect("load");
        let mut frame = vec![0.0f32; 960];
        apm.analyze_reverse(&frame);
        apm.set_stream_delay_ms(0);
        apm.process_capture(&mut frame);
        assert_eq!(frame.len(), 960);
    }
}
