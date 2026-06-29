#![allow(clippy::missing_safety_doc)]

use pc_capture::engine::Engine;
use std::ffi::{c_char, c_float, c_int, CStr};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::{ptr, slice};

pub const PC_ABI_VERSION: c_int = 1;

const _: fn() = || {
    fn assert_sync_send<T: Sync + Send>() {}
    assert_sync_send::<Engine>();
};

#[no_mangle]
pub extern "C" fn pc_abi_version() -> c_int {
    PC_ABI_VERSION
}

#[no_mangle]
pub extern "C" fn pc_engine_new() -> *mut Engine {
    catch_unwind(|| Box::into_raw(Box::new(Engine::new()))).unwrap_or(ptr::null_mut())
}

#[no_mangle]
pub unsafe extern "C" fn pc_engine_free(handle: *mut Engine) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| drop(Box::from_raw(handle))));
}

#[no_mangle]
pub unsafe extern "C" fn pc_control(handle: *mut Engine, json: *const c_char) {
    if handle.is_null() || json.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let engine = &*handle;
        if let Ok(s) = CStr::from_ptr(json).to_str() {
            engine.control(s);
        }
    }));
}

#[no_mangle]
pub unsafe extern "C" fn pc_push_mic(
    handle: *mut Engine,
    samples: *const c_float,
    len: c_int,
) -> c_float {
    if handle.is_null() || samples.is_null() || len <= 0 {
        return 0.0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let engine = &*handle;
        engine.push_mic(slice::from_raw_parts(samples, len as usize))
    }))
    .unwrap_or(0.0)
}

#[no_mangle]
pub unsafe extern "C" fn pc_pull_playback(
    handle: *mut Engine,
    out: *mut c_float,
    cap: c_int,
) -> c_int {
    if handle.is_null() || out.is_null() || cap <= 0 {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let engine = &*handle;
        engine.pull_playback(slice::from_raw_parts_mut(out, cap as usize)) as c_int
    }))
    .unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn pc_mic_level(handle: *mut Engine) -> c_float {
    if handle.is_null() {
        return 0.0;
    }
    catch_unwind(AssertUnwindSafe(|| (*handle).level())).unwrap_or(0.0)
}

#[no_mangle]
pub unsafe extern "C" fn pc_poll_signal(
    handle: *mut Engine,
    out: *mut c_char,
    cap: c_int,
) -> c_int {
    if handle.is_null() || out.is_null() || cap <= 1 {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let engine = &*handle;
        match engine.poll_signal() {
            Some(json) => {
                let bytes = json.as_bytes();
                if bytes.len() + 1 > cap as usize {
                    return -1;
                }
                ptr::copy_nonoverlapping(bytes.as_ptr(), out as *mut u8, bytes.len());
                *out.add(bytes.len()) = 0;

                engine.ack_signal();
                bytes.len() as c_int
            }
            None => 0,
        }
    }))
    .unwrap_or(0)
}
