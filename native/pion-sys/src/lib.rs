use libloading::{Library, Symbol};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, OnceLock};

pub const ABI_VERSION: u32 = 2;
pub const PION_VERSION: u32 = 4_002_017;
pub const PION_VERSION_TEXT: &str = "4.2.17";
pub const STATUS_OK: i32 = 0;
pub const STATUS_EMPTY: i32 = 1;
pub const STATUS_BUFFER_TOO_SMALL: i32 = 2;

static API_CACHE: OnceLock<Mutex<HashMap<PathBuf, Arc<Api>>>> = OnceLock::new();

#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct RtpEvent {
    pub generation: u32,
    pub sequence: u16,
    pub reserved: u16,
    pub timestamp: u32,
    pub peer_len: u32,
    pub payload_len: u32,
    pub arrival_age_ns: u64,
    pub ingress_overflow: u64,
}

#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct SendResult {
    pub attempted: u32,
    pub enqueued: u32,
    pub queue_full: u32,
    pub stale_epoch: u32,
}

#[repr(C)]
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
pub struct TransportCounters {
    pub rtp_tx_attempts: u64,
    pub rtp_tx_ok: u64,
    pub rtp_tx_errors: u64,
    pub rtp_tx_queue_dropped: u64,
    pub rtp_tx_stale_epoch_dropped: u64,
    pub rtp_tx_write_timeouts: u64,
    pub rtp_tx_queue_depth_max: u64,
    pub rtp_rx_packets: u64,
    pub rtp_rx_bytes: u64,
    pub stale_rtp_rx_dropped: u64,
}

type FnAbiVersion = unsafe extern "C" fn() -> u32;
type FnPionVersion = unsafe extern "C" fn() -> u32;
type FnEngineNew = unsafe extern "C" fn() -> u64;
type FnEngineClose = unsafe extern "C" fn(u64) -> i32;
type FnSetIceServers = unsafe extern "C" fn(u64, *mut u8, u32) -> i32;
type FnAddPeer = unsafe extern "C" fn(u64, *mut u8, u32, u32, u32, u32, u64) -> i32;
type FnRemovePeer = unsafe extern "C" fn(u64, *mut u8, u32) -> i32;
type FnSetRemoteSdp =
    unsafe extern "C" fn(u64, *mut u8, u32, u32, *mut u8, u32, *mut u8, u32) -> i32;
type FnAddIceCandidate = unsafe extern "C" fn(u64, *mut u8, u32, u32, *mut u8, u32) -> i32;
type FnRestartIce = unsafe extern "C" fn(u64, *mut u8, u32, u32, u32, u32) -> i32;
type FnSendOpus = unsafe extern "C" fn(u64, *mut u8, u32, u64, u64, *mut SendResult) -> i32;
type FnAdvanceEpoch = unsafe extern "C" fn(u64, u64, u32) -> i32;
type FnPollControl = unsafe extern "C" fn(u64, *mut u8, u32, *mut u32) -> i32;
type FnPollRtp = unsafe extern "C" fn(u64, *mut RtpEvent, *mut u8, u32, *mut u8, u32) -> i32;
type FnGetCounters = unsafe extern "C" fn(u64, *mut TransportCounters) -> i32;

pub struct Api {
    // A Go c-shared runtime cannot be safely unloaded. Keep every successfully loaded module
    // mapped until process exit, after individual engine handles have been closed.
    _library: &'static Library,
    abi_version: FnAbiVersion,
    pion_version: FnPionVersion,
    engine_new: FnEngineNew,
    engine_close: FnEngineClose,
    set_ice_servers: FnSetIceServers,
    add_peer: FnAddPeer,
    remove_peer: FnRemovePeer,
    set_remote_sdp: FnSetRemoteSdp,
    add_ice_candidate: FnAddIceCandidate,
    restart_ice: FnRestartIce,
    send_opus: FnSendOpus,
    advance_epoch: FnAdvanceEpoch,
    poll_control: FnPollControl,
    poll_rtp: FnPollRtp,
    get_counters: FnGetCounters,
}

impl Api {
    /// Loads the Go shared library and copies all function pointers while retaining the
    /// library for the process lifetime of this API value. A Go runtime-bearing library must
    /// never be unloaded while any exported call or Pion goroutine is still active.
    ///
    /// # Safety
    ///
    /// `path` must identify a trusted PerfectComms Pion library. Loading a native library runs
    /// its initialization code, and this function intentionally keeps that module loaded until
    /// process exit after verifying its complete ABI and pinned Pion version.
    pub unsafe fn load(path: &Path) -> Result<Self, String> {
        // Leak immediately after a successful OS load, including symbol/ABI error paths. Merely
        // loading a Go c-shared module starts its runtime, which is not safe to unload later.
        let library: &'static Library = Box::leak(Box::new(
            Library::new(path).map_err(|error| format!("load:{error}"))?,
        ));
        macro_rules! symbol {
            ($name:expr) => {{
                let value: Symbol<_> = library.get($name).map_err(|error| {
                    format!("symbol {}:{error}", String::from_utf8_lossy($name))
                })?;
                *value
            }};
        }
        let api = Self {
            abi_version: symbol!(b"pc_pion_abi_version"),
            pion_version: symbol!(b"pc_pion_version"),
            engine_new: symbol!(b"pc_pion_engine_new"),
            engine_close: symbol!(b"pc_pion_engine_close"),
            set_ice_servers: symbol!(b"pc_pion_set_ice_servers"),
            add_peer: symbol!(b"pc_pion_add_peer"),
            remove_peer: symbol!(b"pc_pion_remove_peer"),
            set_remote_sdp: symbol!(b"pc_pion_set_remote_sdp"),
            add_ice_candidate: symbol!(b"pc_pion_add_ice_candidate"),
            restart_ice: symbol!(b"pc_pion_restart_ice"),
            send_opus: symbol!(b"pc_pion_send_opus"),
            advance_epoch: symbol!(b"pc_pion_advance_epoch"),
            poll_control: symbol!(b"pc_pion_poll_control"),
            poll_rtp: symbol!(b"pc_pion_poll_rtp"),
            get_counters: symbol!(b"pc_pion_get_counters"),
            _library: library,
        };
        let actual = (api.abi_version)();
        if actual != ABI_VERSION {
            return Err(format!("abi:{actual}:expected:{ABI_VERSION}"));
        }
        let actual_pion = (api.pion_version)();
        if actual_pion != PION_VERSION {
            return Err(format!(
                "pion-version:{actual_pion}:expected:{PION_VERSION}"
            ));
        }
        Ok(api)
    }

    pub fn load_default(explicit: Option<&Path>) -> Result<(Arc<Self>, PathBuf), String> {
        let mut candidates = Vec::new();
        if let Some(path) = explicit {
            candidates.push(path.to_path_buf());
        } else {
            // Production resolves only the content-matched library staged beside the helper.
            // Never let an inherited environment variable redirect native code loading.
            if let Ok(executable) = std::env::current_exe() {
                if let Some(directory) = executable.parent() {
                    candidates.push(directory.join(platform_library_name()));
                }
            }
        }
        let mut failures = Vec::new();
        candidates.dedup();
        for path in candidates {
            let resolved = std::fs::canonicalize(&path).unwrap_or_else(|_| path.clone());
            let cache = API_CACHE.get_or_init(|| Mutex::new(HashMap::new()));
            let mut loaded = cache.lock().unwrap();
            if let Some(api) = loaded.get(&resolved) {
                return Ok((api.clone(), resolved));
            }
            match unsafe { Self::load(&resolved) } {
                Ok(api) => {
                    let api = Arc::new(api);
                    loaded.insert(resolved.clone(), api.clone());
                    return Ok((api, resolved));
                }
                Err(error) => failures.push(format!("{}={error}", path.display())),
            }
        }
        Err(format!(
            "Pion transport library unavailable [{}]",
            failures.join("; ")
        ))
    }

    pub fn engine_new(&self) -> Result<u64, i32> {
        let handle = unsafe { (self.engine_new)() };
        if handle == 0 {
            Err(-7)
        } else {
            Ok(handle)
        }
    }

    pub fn engine_close(&self, handle: u64) -> i32 {
        unsafe { (self.engine_close)(handle) }
    }

    pub fn set_ice_servers(&self, handle: u64, json: &[u8]) -> i32 {
        with_bytes(json, |pointer, length| unsafe {
            (self.set_ice_servers)(handle, pointer, length)
        })
    }

    pub fn add_peer(
        &self,
        handle: u64,
        peer_id: &str,
        offerer: bool,
        relay_only: bool,
        generation: u32,
        min_epoch: u64,
    ) -> i32 {
        with_bytes(peer_id.as_bytes(), |pointer, length| unsafe {
            (self.add_peer)(
                handle,
                pointer,
                length,
                u32::from(offerer),
                u32::from(relay_only),
                generation,
                min_epoch,
            )
        })
    }

    pub fn remove_peer(&self, handle: u64, peer_id: &str) -> i32 {
        with_bytes(peer_id.as_bytes(), |pointer, length| unsafe {
            (self.remove_peer)(handle, pointer, length)
        })
    }

    pub fn set_remote_sdp(
        &self,
        handle: u64,
        peer_id: &str,
        generation: u32,
        sdp_type: &str,
        sdp: &str,
    ) -> i32 {
        with_bytes(peer_id.as_bytes(), |peer_pointer, peer_length| {
            with_bytes(sdp_type.as_bytes(), |type_pointer, type_length| {
                with_bytes(sdp.as_bytes(), |sdp_pointer, sdp_length| unsafe {
                    (self.set_remote_sdp)(
                        handle,
                        peer_pointer,
                        peer_length,
                        generation,
                        type_pointer,
                        type_length,
                        sdp_pointer,
                        sdp_length,
                    )
                })
            })
        })
    }

    pub fn add_ice_candidate(
        &self,
        handle: u64,
        peer_id: &str,
        generation: u32,
        candidate: &str,
    ) -> i32 {
        with_bytes(peer_id.as_bytes(), |peer_pointer, peer_length| {
            with_bytes(
                candidate.as_bytes(),
                |candidate_pointer, candidate_length| unsafe {
                    (self.add_ice_candidate)(
                        handle,
                        peer_pointer,
                        peer_length,
                        generation,
                        candidate_pointer,
                        candidate_length,
                    )
                },
            )
        })
    }

    pub fn restart_ice(
        &self,
        handle: u64,
        peer_id: &str,
        generation: u32,
        relay_only: bool,
        create_offer: bool,
    ) -> i32 {
        with_bytes(peer_id.as_bytes(), |pointer, length| unsafe {
            (self.restart_ice)(
                handle,
                pointer,
                length,
                generation,
                u32::from(relay_only),
                u32::from(create_offer),
            )
        })
    }

    pub fn send_opus(
        &self,
        handle: u64,
        payload: &[u8],
        epoch: u64,
        media_sequence: u64,
    ) -> Result<SendResult, i32> {
        let mut result = SendResult::default();
        let status = with_bytes(payload, |pointer, length| unsafe {
            (self.send_opus)(handle, pointer, length, epoch, media_sequence, &mut result)
        });
        if status == STATUS_OK {
            Ok(result)
        } else {
            Err(status)
        }
    }

    pub fn advance_epoch(&self, handle: u64, epoch: u64, timeout_ms: u32) -> i32 {
        unsafe { (self.advance_epoch)(handle, epoch, timeout_ms) }
    }

    pub fn poll_control(&self, handle: u64, buffer: &mut Vec<u8>) -> Result<bool, i32> {
        if buffer.capacity() == 0 {
            buffer.reserve(4096);
        }
        buffer.resize(buffer.capacity(), 0);
        let mut required = 0u32;
        let mut status = unsafe {
            (self.poll_control)(
                handle,
                buffer.as_mut_ptr(),
                buffer.len().try_into().unwrap_or(u32::MAX),
                &mut required,
            )
        };
        if status == STATUS_BUFFER_TOO_SMALL {
            buffer.resize(required as usize, 0);
            status = unsafe {
                (self.poll_control)(handle, buffer.as_mut_ptr(), required, &mut required)
            };
        }
        match status {
            STATUS_OK => {
                buffer.truncate(required as usize);
                Ok(true)
            }
            STATUS_EMPTY => {
                buffer.clear();
                Ok(false)
            }
            error => {
                buffer.clear();
                Err(error)
            }
        }
    }

    pub fn poll_rtp(
        &self,
        handle: u64,
        peer_buffer: &mut Vec<u8>,
        payload_buffer: &mut Vec<u8>,
    ) -> Result<Option<RtpEvent>, i32> {
        peer_buffer.resize(peer_buffer.capacity().max(64), 0);
        payload_buffer.resize(payload_buffer.capacity().max(2048), 0);
        let mut event = RtpEvent::default();
        let mut status = unsafe {
            (self.poll_rtp)(
                handle,
                &mut event,
                peer_buffer.as_mut_ptr(),
                peer_buffer.len().try_into().unwrap_or(u32::MAX),
                payload_buffer.as_mut_ptr(),
                payload_buffer.len().try_into().unwrap_or(u32::MAX),
            )
        };
        if status == STATUS_BUFFER_TOO_SMALL {
            peer_buffer.resize(event.peer_len as usize, 0);
            payload_buffer.resize(event.payload_len as usize, 0);
            status = unsafe {
                (self.poll_rtp)(
                    handle,
                    &mut event,
                    peer_buffer.as_mut_ptr(),
                    event.peer_len,
                    payload_buffer.as_mut_ptr(),
                    event.payload_len,
                )
            };
        }
        match status {
            STATUS_OK => {
                peer_buffer.truncate(event.peer_len as usize);
                payload_buffer.truncate(event.payload_len as usize);
                Ok(Some(event))
            }
            STATUS_EMPTY => {
                peer_buffer.clear();
                payload_buffer.clear();
                Ok(None)
            }
            error => Err(error),
        }
    }

    pub fn counters(&self, handle: u64) -> Result<TransportCounters, i32> {
        let mut counters = TransportCounters::default();
        let status = unsafe { (self.get_counters)(handle, &mut counters) };
        if status == STATUS_OK {
            Ok(counters)
        } else {
            Err(status)
        }
    }
}

fn with_bytes(bytes: &[u8], call: impl FnOnce(*mut u8, u32) -> i32) -> i32 {
    let Ok(length) = u32::try_from(bytes.len()) else {
        return -2;
    };
    let pointer = if bytes.is_empty() {
        std::ptr::null_mut()
    } else {
        bytes.as_ptr().cast_mut()
    };
    call(pointer, length)
}

pub const fn platform_library_name() -> &'static str {
    #[cfg(all(target_os = "windows", target_arch = "x86_64"))]
    {
        "pc-pion.x64.dll"
    }
    #[cfg(all(target_os = "windows", target_arch = "x86"))]
    {
        "pc-pion.x86.dll"
    }
    #[cfg(target_os = "linux")]
    {
        "libpc-pion.so"
    }
    #[cfg(target_os = "android")]
    {
        "libpc-pion.so"
    }
    #[cfg(target_os = "macos")]
    {
        "libpc-pion.dylib"
    }
}

#[cfg(test)]
mod tests {
    use super::{
        platform_library_name, Api, RtpEvent, SendResult, TransportCounters, PION_VERSION,
        PION_VERSION_TEXT, STATUS_OK,
    };
    use std::path::Path;

    #[test]
    fn abi_struct_layouts_are_stable() {
        assert_eq!(std::mem::size_of::<RtpEvent>(), 40);
        assert_eq!(std::mem::size_of::<SendResult>(), 16);
        assert_eq!(std::mem::size_of::<TransportCounters>(), 80);
    }

    #[test]
    fn platform_name_is_not_empty() {
        assert!(!platform_library_name().is_empty());
        assert_eq!(PION_VERSION, 4_002_017);
        assert_eq!(PION_VERSION_TEXT, "4.2.17");
    }

    #[test]
    fn configured_library_loads_and_creates_engine() {
        if std::env::var_os("PC_REQUIRE_PION").is_none() {
            return;
        }
        let configured = std::env::var_os("PC_PION_LIB")
            .expect("PC_REQUIRE_PION requires an explicit PC_PION_LIB test path");
        let (api, _) = Api::load_default(Some(Path::new(&configured)))
            .expect("configured Pion transport library must load");
        let handle = api.engine_new().expect("Pion engine must be created");
        assert_eq!(api.engine_close(handle), STATUS_OK);
    }
}
