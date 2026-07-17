#![allow(clippy::missing_safety_doc)]

use pc_capture::engine::Engine;
use std::ffi::{c_char, c_float, c_int, CStr};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::{AtomicBool, Ordering};
use std::{ptr, slice};

// ABI 3 adds protocol-7 input/synthetic controls and bounded peer-level telemetry.
pub const PC_ABI_VERSION: c_int = 3;

// Release packaging reads this exported, NUL-terminated marker directly from the ELF file.
// Keep its decimal value in sync with PC_ABI_VERSION and scripts/verify-release-assets.py.
#[used]
#[no_mangle]
pub static PC_MOBILE_ABI_MARKER: [u8; 29] = *b"PERFECTCOMMS_PC_MOBILE_ABI=3\0";

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
        Some(Self {
            engine: Engine::try_new().ok()?,
            healthy: AtomicBool::new(true),
        })
    }

    fn is_healthy(&self) -> bool {
        self.healthy.load(Ordering::Acquire)
    }

    fn mark_unhealthy(&self) {
        self.healthy.store(false, Ordering::Release);
    }
}

#[no_mangle]
pub extern "C" fn pc_abi_version() -> c_int {
    PC_ABI_VERSION
}

#[no_mangle]
pub extern "C" fn pc_engine_new() -> *mut MobileEngine {
    catch_unwind(|| {
        MobileEngine::try_new()
            .map(|engine| Box::into_raw(Box::new(engine)))
            .unwrap_or(ptr::null_mut())
    })
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
        // ABI 3 contract: -1 means the caller must grow its buffer and poll again. The signal
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
        assert_eq!(PC_ABI_VERSION, 3);
        assert_eq!(pc_abi_version(), 3);
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
            assert_eq!(pc_pull_playback(ptr::null_mut(), ptr::null_mut(), 0), 0);
            assert_eq!(pc_mic_level(ptr::null_mut()), 0.0);
            assert_eq!(pc_poll_signal(ptr::null_mut(), ptr::null_mut(), 0), 0);
        }
    }

    #[test]
    fn engine_creation_requires_a_working_opus_codec() {
        let handle = pc_engine_new();
        assert!(!handle.is_null());
        unsafe { pc_engine_free(handle) };
    }
}
