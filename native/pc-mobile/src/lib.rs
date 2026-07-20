#![allow(clippy::missing_safety_doc)]

use pc_capture::engine::Engine;
use std::ffi::{c_char, c_float, c_int, CStr};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;
use std::{ptr, slice};

// ABI 5 adds capture-gap-aware microphone pushes. Android must preserve dropped microphone time
// so RTP concealment remains chronological instead of splicing nonadjacent PCM.
pub const PC_ABI_VERSION: c_int = 5;

// Release packaging reads this exported, NUL-terminated marker directly from the ELF file.
// Keep its decimal value in sync with PC_ABI_VERSION and scripts/verify-release-assets.py.
#[used]
#[no_mangle]
pub static PC_MOBILE_ABI_MARKER: [u8; 29] = *b"PERFECTCOMMS_PC_MOBILE_ABI=5\0";

// Serializes the global transport-path update with engine construction. The Pion loader copies
// the path, so the managed UTF-8 buffer only needs to remain valid for the FFI call itself.
static ENGINE_CREATE_GATE: Mutex<()> = Mutex::new(());

const _: fn() = || {
    fn assert_sync_send<T: Sync + Send>() {}
    assert_sync_send::<Engine>();
};

pub struct MobileEngine {
    engine: Engine,
    healthy: AtomicBool,
}

impl MobileEngine {
    fn try_new() -> Option<Self> {
        let engine = Engine::try_new().ok()?;
        if !engine.transport_ready() {
            eprintln!(
                "pc-mobile: Pion transport initialization failed: {}",
                engine.transport_error().unwrap_or("transport-unavailable")
            );
            return None;
        }
        Some(Self {
            engine,
            healthy: AtomicBool::new(true),
        })
    }

    fn is_healthy(&self) -> bool {
        self.healthy.load(Ordering::Acquire) && self.engine.transport_ready()
    }

    fn mark_unhealthy(&self) {
        self.healthy.store(false, Ordering::Release);
    }
}

fn create_engine(transport_path: Option<&Path>) -> *mut MobileEngine {
    // Construction panics are caught at the ABI boundary. Recover this narrow path/configuration
    // gate as well so a transient thread-spawn failure cannot disable every later retry.
    let _gate = ENGINE_CREATE_GATE
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    if let Some(path) = transport_path {
        pc_capture::rtc::set_transport_library_path(Some(path));
    }
    MobileEngine::try_new()
        .map(|engine| Box::into_raw(Box::new(engine)))
        .unwrap_or(ptr::null_mut())
}

unsafe fn parse_transport_path(path: *const c_char) -> Option<PathBuf> {
    if path.is_null() {
        return None;
    }
    let text = CStr::from_ptr(path).to_str().ok()?;
    if text.is_empty() {
        return None;
    }
    let path = PathBuf::from(text);
    // Android passes either the content-addressed cache extraction or the APK native-library
    // directory. Requiring an absolute path prevents cwd/search-path hijacking.
    path.is_absolute().then_some(path)
}

#[no_mangle]
pub extern "C" fn pc_abi_version() -> c_int {
    PC_ABI_VERSION
}

#[no_mangle]
pub extern "C" fn pc_engine_new() -> *mut MobileEngine {
    catch_unwind(|| create_engine(None)).unwrap_or(ptr::null_mut())
}

#[no_mangle]
pub unsafe extern "C" fn pc_engine_new_with_transport(
    transport_path: *const c_char,
) -> *mut MobileEngine {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(path) = parse_transport_path(transport_path) else {
            return ptr::null_mut();
        };
        create_engine(Some(&path))
    }))
    .unwrap_or(ptr::null_mut())
}

#[no_mangle]
pub unsafe extern "C" fn pc_engine_free(handle: *mut MobileEngine) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| drop(Box::from_raw(handle))));
}

#[no_mangle]
pub unsafe extern "C" fn pc_control(handle: *mut MobileEngine, json: *const c_char) {
    if handle.is_null() || json.is_null() {
        return;
    }
    let mobile = &*handle;
    if !mobile.is_healthy() {
        return;
    }
    if catch_unwind(AssertUnwindSafe(|| {
        if let Ok(s) = CStr::from_ptr(json).to_str() {
            mobile.engine.control(s);
        }
    }))
    .is_err()
    {
        mobile.mark_unhealthy();
    }
}

#[no_mangle]
pub unsafe extern "C" fn pc_push_mic(
    handle: *mut MobileEngine,
    samples: *const c_float,
    len: c_int,
) -> c_float {
    if handle.is_null() || samples.is_null() || len <= 0 {
        return 0.0;
    }
    let mobile = &*handle;
    if !mobile.is_healthy() {
        return 0.0;
    }
    match catch_unwind(AssertUnwindSafe(|| {
        mobile
            .engine
            .push_mic(slice::from_raw_parts(samples, len as usize))
    })) {
        Ok(level) => level,
        Err(_) => {
            mobile.mark_unhealthy();
            0.0
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn pc_push_mic_with_gap(
    handle: *mut MobileEngine,
    samples: *const c_float,
    len: c_int,
    skipped_before_current: u64,
) -> c_float {
    if handle.is_null() || samples.is_null() || len <= 0 {
        return 0.0;
    }
    let mobile = &*handle;
    if !mobile.is_healthy() {
        return 0.0;
    }
    match catch_unwind(AssertUnwindSafe(|| {
        mobile.engine.push_mic_with_media_gap(
            slice::from_raw_parts(samples, len as usize),
            skipped_before_current,
        )
    })) {
        Ok(level) => level,
        Err(_) => {
            mobile.mark_unhealthy();
            0.0
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn pc_pull_playback(
    handle: *mut MobileEngine,
    out: *mut c_float,
    cap: c_int,
) -> c_int {
    if handle.is_null() || out.is_null() || cap <= 0 {
        return 0;
    }
    let mobile = &*handle;
    if !mobile.is_healthy() {
        return 0;
    }
    match catch_unwind(AssertUnwindSafe(|| {
        mobile
            .engine
            .pull_playback(slice::from_raw_parts_mut(out, cap as usize)) as c_int
    })) {
        Ok(written) => written,
        Err(_) => {
            mobile.mark_unhealthy();
            0
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn pc_mic_level(handle: *mut MobileEngine) -> c_float {
    if handle.is_null() {
        return 0.0;
    }
    let mobile = &*handle;
    if !mobile.is_healthy() {
        return c_float::NAN;
    }
    match catch_unwind(AssertUnwindSafe(|| mobile.engine.level())) {
        Ok(level) => level,
        Err(_) => {
            mobile.mark_unhealthy();
            c_float::NAN
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn pc_poll_signal(
    handle: *mut MobileEngine,
    out: *mut c_char,
    cap: c_int,
) -> c_int {
    if handle.is_null() || out.is_null() || cap <= 1 {
        return 0;
    }
    let mobile = &*handle;
    if !mobile.is_healthy() {
        return 0;
    }
    match catch_unwind(AssertUnwindSafe(|| match mobile.engine.poll_signal() {
        Some(json) => {
            let written = copy_signal_to_buffer(&json, out, cap);
            if written < 0 {
                return written;
            }
            mobile.engine.ack_signal();
            written
        }
        None => 0,
    })) {
        Ok(written) => written,
        Err(_) => {
            mobile.mark_unhealthy();
            0
        }
    }
}

unsafe fn copy_signal_to_buffer(json: &str, out: *mut c_char, cap: c_int) -> c_int {
    let bytes = json.as_bytes();
    if bytes.len() + 1 > cap as usize {
        // ABI 4 contract: -1 means the caller must grow its buffer and poll again. The signal
        // remains pending because pc_poll_signal acknowledges only after a successful copy.
        return -1;
    }
    ptr::copy_nonoverlapping(bytes.as_ptr(), out as *mut u8, bytes.len());
    *out.add(bytes.len()) = 0;
    bytes.len() as c_int
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn abi_version_matches_contract() {
        assert_eq!(PC_ABI_VERSION, 5);
        assert_eq!(pc_abi_version(), 5);
        assert_eq!(
            PC_MOBILE_ABI_MARKER.as_slice(),
            format!("PERFECTCOMMS_PC_MOBILE_ABI={PC_ABI_VERSION}\0").as_bytes()
        );
    }

    #[test]
    fn oversized_signal_requests_a_larger_buffer_without_partial_copy() {
        let mut small = [b'x' as c_char; 4];
        let written = unsafe { copy_signal_to_buffer("oversized", small.as_mut_ptr(), 4) };
        assert_eq!(written, -1);
        assert_eq!(small, [b'x' as c_char; 4]);

        let mut exact = [0 as c_char; 10];
        let written = unsafe { copy_signal_to_buffer("oversized", exact.as_mut_ptr(), 10) };
        assert_eq!(written, 9);
        assert_eq!(exact[9], 0);
    }

    #[test]
    fn null_handles_are_safe_at_ffi_boundary() {
        unsafe {
            pc_engine_free(ptr::null_mut());
            pc_control(ptr::null_mut(), ptr::null());
            assert_eq!(pc_push_mic(ptr::null_mut(), ptr::null(), 0), 0.0);
            assert_eq!(
                pc_push_mic_with_gap(ptr::null_mut(), ptr::null(), 0, 1),
                0.0
            );
            assert_eq!(pc_pull_playback(ptr::null_mut(), ptr::null_mut(), 0), 0);
            assert_eq!(pc_mic_level(ptr::null_mut()), 0.0);
            assert_eq!(pc_poll_signal(ptr::null_mut(), ptr::null_mut(), 0), 0);
        }
    }

    #[test]
    fn transport_constructor_rejects_null_relative_and_non_utf8_paths() {
        unsafe {
            assert!(pc_engine_new_with_transport(ptr::null()).is_null());

            let relative = b"libpc-pion.so\0";
            assert!(pc_engine_new_with_transport(relative.as_ptr().cast()).is_null());

            let non_utf8 = [0xff_u8, 0];
            assert!(pc_engine_new_with_transport(non_utf8.as_ptr().cast()).is_null());
        }
    }

    #[test]
    fn engine_creation_requires_a_working_opus_codec() {
        let configured = std::env::var_os("PC_PION_LIB");
        let transport = configured.as_ref().map(|path| {
            std::ffi::CString::new(path.to_string_lossy().as_bytes())
                .expect("Pion test path contains NUL")
        });
        let handle = match transport {
            Some(path) => unsafe { pc_engine_new_with_transport(path.as_ptr()) },
            None => pc_engine_new(),
        };
        assert!(!handle.is_null());
        unsafe { pc_engine_free(handle) };
    }
}
