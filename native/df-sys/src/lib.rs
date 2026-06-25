use libloading::{Library, Symbol};
use std::ffi::CString;
use std::os::raw::{c_char, c_float};

#[repr(C)]
pub struct DFState {
    _p: [u8; 0],
}

type FnCreate = unsafe extern "C" fn(*const c_char, c_float, *const c_char) -> *mut DFState;
type FnFrameLength = unsafe extern "C" fn(*mut DFState) -> usize;
type FnProcess = unsafe extern "C" fn(*mut DFState, *mut c_float, *mut c_float) -> c_float;
type FnFree = unsafe extern "C" fn(*mut DFState);

struct Api {
    _lib: Library,
    create: FnCreate,
    frame_length: FnFrameLength,
    process: FnProcess,
    free: FnFree,
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
            create: sym!(b"df_create"),
            frame_length: sym!(b"df_get_frame_length"),
            process: sym!(b"df_process_frame"),
            free: sym!(b"df_free"),
            _lib: lib,
        })
    }
}

const FRAME: usize = 960;
const ATTEN_LIM_DB: f32 = 18.0;

pub struct Ns {
    api: Api,
    state: *mut DFState,
    hop: usize,
    out: Vec<f32>,
}

unsafe impl Send for Ns {}

impl Ns {
    pub fn load(lib_path: &str) -> Result<Ns, String> {
        unsafe {
            let api = Api::load(lib_path)?;
            let empty = CString::new("").unwrap();
            let state = (api.create)(empty.as_ptr(), ATTEN_LIM_DB, std::ptr::null());
            if state.is_null() {
                return Err("df-create".into());
            }
            let hop = (api.frame_length)(state);
            if hop == 0 || FRAME % hop != 0 {
                (api.free)(state);
                return Err(format!("hop:{hop}"));
            }
            Ok(Ns {
                api,
                state,
                hop,
                out: vec![0.0; hop],
            })
        }
    }

    pub fn frame_length(&self) -> usize {
        self.hop
    }

    pub fn process(&mut self, frame: &mut [f32]) {
        unsafe {
            let mut off = 0;
            while off + self.hop <= frame.len() {
                let ip = frame.as_mut_ptr().add(off);
                let op = self.out.as_mut_ptr();
                (self.api.process)(self.state, ip, op);
                std::ptr::copy_nonoverlapping(self.out.as_ptr(), frame.as_mut_ptr().add(off), self.hop);
                off += self.hop;
            }
        }
    }
}

impl Drop for Ns {
    fn drop(&mut self) {
        unsafe {
            if !self.state.is_null() {
                (self.api.free)(self.state);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::Ns;

    #[test]
    #[ignore]
    fn loads_and_processes_a_frame() {
        let path = std::env::var("DF_LIB").expect("set DF_LIB to the deep-filter shared library path");
        let mut ns = Ns::load(&path).expect("load");
        assert!(ns.frame_length() > 0);
        let mut frame = vec![0.0f32; 960];
        ns.process(&mut frame);
        assert_eq!(frame.len(), 960);
    }
}
