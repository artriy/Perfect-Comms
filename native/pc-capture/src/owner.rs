//! Parent-process lifetime monitoring for the desktop sidecar.
//!
//! Native Windows builds hold a process handle, so PID reuse cannot hide owner exit.
//! Unix builds poll `kill(pid, 0)`; Linux/macOS both provide the required semantics.
//! Wine launches a host-native Unix helper from a Windows guest process, so the guest PID
//! is not assumed to be a host PID. Wine launchers should omit `--owner-pid` and rely on the
//! control-socket EOF hard deadline in `ipc::run_session` instead.

use std::io;

pub fn spawn_owner_guard(owner_pid: u32) -> io::Result<()> {
    if owner_pid == 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "owner PID must be positive",
        ));
    }

    let monitor = OwnerMonitor::open(owner_pid)?;
    std::thread::Builder::new()
        .name("pc-capture-owner-guard".to_string())
        .spawn(move || {
            let result = monitor.wait_for_exit();
            match result {
                Ok(()) => eprintln!("pc-capture: owner exited pid={owner_pid}; terminating"),
                Err(error) => eprintln!(
                    "pc-capture: owner guard failed pid={owner_pid} error={error}; terminating fail-safe"
                ),
            }
            std::process::exit(0);
        })?;
    Ok(())
}

#[cfg(unix)]
#[derive(Debug)]
struct OwnerMonitor {
    pid: libc::pid_t,
}

#[cfg(unix)]
impl OwnerMonitor {
    fn open(owner_pid: u32) -> io::Result<Self> {
        let pid = libc::pid_t::try_from(owner_pid).map_err(|_| {
            io::Error::new(io::ErrorKind::InvalidInput, "owner PID is out of range")
        })?;
        match unix_process_status(pid)? {
            ProcessStatus::Alive => Ok(Self { pid }),
            ProcessStatus::Exited => Err(io::Error::new(
                io::ErrorKind::NotFound,
                "owner process is not running",
            )),
        }
    }

    fn wait_for_exit(self) -> io::Result<()> {
        loop {
            std::thread::sleep(std::time::Duration::from_millis(500));
            if unix_process_status(self.pid)? == ProcessStatus::Exited {
                return Ok(());
            }
        }
    }
}

#[cfg(unix)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum ProcessStatus {
    Alive,
    Exited,
}

#[cfg(unix)]
fn unix_process_status(pid: libc::pid_t) -> io::Result<ProcessStatus> {
    if unsafe { libc::kill(pid, 0) } == 0 {
        return Ok(ProcessStatus::Alive);
    }
    match io::Error::last_os_error().raw_os_error() {
        Some(libc::EPERM) => Ok(ProcessStatus::Alive),
        Some(libc::ESRCH) => Ok(ProcessStatus::Exited),
        _ => Err(io::Error::last_os_error()),
    }
}

#[cfg(windows)]
#[derive(Debug)]
struct OwnerMonitor {
    handle: usize,
}

#[cfg(windows)]
impl OwnerMonitor {
    fn open(owner_pid: u32) -> io::Result<Self> {
        let handle = unsafe { OpenProcess(SYNCHRONIZE, 0, owner_pid) };
        if handle.is_null() {
            return Err(io::Error::last_os_error());
        }
        Ok(Self {
            handle: handle as usize,
        })
    }

    fn wait_for_exit(self) -> io::Result<()> {
        match unsafe { WaitForSingleObject(self.handle as *mut std::ffi::c_void, INFINITE) } {
            WAIT_OBJECT_0 => Ok(()),
            _ => Err(io::Error::last_os_error()),
        }
    }
}

#[cfg(windows)]
impl Drop for OwnerMonitor {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.handle as *mut std::ffi::c_void);
        }
    }
}

#[cfg(windows)]
const SYNCHRONIZE: u32 = 0x0010_0000;
#[cfg(windows)]
const INFINITE: u32 = 0xffff_ffff;
#[cfg(windows)]
const WAIT_OBJECT_0: u32 = 0;

#[cfg(windows)]
#[link(name = "kernel32")]
extern "system" {
    fn OpenProcess(
        desired_access: u32,
        inherit_handle: i32,
        process_id: u32,
    ) -> *mut std::ffi::c_void;
    fn WaitForSingleObject(handle: *mut std::ffi::c_void, milliseconds: u32) -> u32;
    fn CloseHandle(handle: *mut std::ffi::c_void) -> i32;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn current_process_can_be_opened_as_owner() {
        let monitor = OwnerMonitor::open(std::process::id());
        assert!(
            monitor.is_ok(),
            "current process should be observable: {monitor:?}"
        );
    }

    #[test]
    fn zero_owner_pid_is_rejected_before_spawning_thread() {
        let error = spawn_owner_guard(0).unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::InvalidInput);
    }
}
