//! Optional, process-local window used by OBS Application Audio Capture.

use std::ffi::c_void;
use std::io;
use std::ptr;
use std::sync::mpsc;
use std::thread::{self, JoinHandle};

pub struct ObsCaptureWindow {
    gui: Option<GuiThread>,
}

impl ObsCaptureWindow {
    pub fn new() -> Self {
        Self { gui: None }
    }

    pub fn set_enabled(&mut self, enabled: bool) -> io::Result<()> {
        if enabled {
            if let Some(gui) = self.gui.as_ref() {
                if !gui.thread.is_finished() {
                    return Ok(());
                }
                return match self.gui.take().expect("GUI ownership was checked").join() {
                    Ok(()) => Err(io::Error::other("OBS window thread exited unexpectedly")),
                    Err(error) => Err(error),
                };
            }
            self.gui = Some(GuiThread::spawn()?);
            return Ok(());
        }

        if let Some(gui) = self.gui.as_ref() {
            if !gui.thread.is_finished() {
                // SAFETY: this event remains owned until the GUI thread is joined.
                if unsafe { SetEvent(gui.stop_event as *mut c_void) } == 0 {
                    return Err(win32_error("signal OBS window shutdown"));
                }
            }
        }
        match self.gui.take() {
            Some(gui) => gui.join(),
            None => Ok(()),
        }
    }
}

impl Default for ObsCaptureWindow {
    fn default() -> Self {
        Self::new()
    }
}

impl Drop for ObsCaptureWindow {
    fn drop(&mut self) {
        if let Err(error) = self.set_enabled(false) {
            eprintln!("pc-capture: OBS window cleanup failed: {error}");
            // A failed signal must not detach GUI ownership during Drop.
            if let Some(gui) = self.gui.take() {
                if let Err(error) = gui.join() {
                    eprintln!("pc-capture: OBS window join failed: {error}");
                }
            }
        }
    }
}

struct GuiThread {
    stop_event: usize,
    thread: JoinHandle<io::Result<()>>,
    #[cfg(test)]
    window: usize,
}

impl GuiThread {
    fn spawn() -> io::Result<Self> {
        // SAFETY: unnamed, manual-reset event; no borrowed attributes or name.
        let stop_event = unsafe { CreateEventW(ptr::null(), 1, 0, ptr::null()) };
        if stop_event.is_null() {
            return Err(win32_error("create OBS window shutdown event"));
        }
        let stop_event = stop_event as usize;
        let (ready_tx, ready_rx) = mpsc::sync_channel(1);
        let thread = match thread::Builder::new()
            .name("pc-capture-obs-window".to_string())
            .spawn(move || run_window(stop_event, ready_tx))
        {
            Ok(thread) => thread,
            Err(error) => return merge(Err(error), close_event(stop_event)),
        };
        let gui = Self {
            stop_event,
            thread,
            #[cfg(test)]
            window: 0,
        };
        match ready_rx.recv() {
            Ok(_window) => Ok(Self {
                #[cfg(test)]
                window: _window,
                ..gui
            }),
            Err(_) => match gui.join() {
                Err(error) => Err(error),
                Ok(()) => Err(io::Error::other(
                    "OBS window thread exited before readiness",
                )),
            },
        }
    }

    fn join(self) -> io::Result<()> {
        let stopped = self
            .thread
            .join()
            .unwrap_or_else(|_| Err(io::Error::other("OBS window thread panicked")));
        merge(stopped, close_event(self.stop_event))
    }
}

fn run_window(stop_event: usize, ready: mpsc::SyncSender<usize>) -> io::Result<()> {
    let class_name: Vec<u16> = "PerfectCommsObsAudioWindow\0".encode_utf16().collect();
    let title: Vec<u16> = "Perfect Comms Audio\0".encode_utf16().collect();
    let instructions: Vec<u16> = concat!(
        "Keep this window minimized while streaming.\r\n\r\n",
        "In OBS, add Application Audio Capture and select Perfect Comms Audio.\r\n\r\n",
        "Closing this window minimizes it. To remove it, turn off OBS Compatibility Mode in Perfect Comms.\0"
    )
    .encode_utf16()
    .collect();
    let static_class: Vec<u16> = "STATIC\0".encode_utf16().collect();

    // SAFETY: null requests the current executable's loaded module.
    let instance = unsafe { GetModuleHandleW(ptr::null()) };
    if instance.is_null() {
        return Err(win32_error("locate OBS window module"));
    }
    // SAFETY: IDC_ARROW is a predefined, shared system cursor resource.
    let cursor = unsafe { LoadCursorW(ptr::null_mut(), 32512usize as *const u16) };
    if cursor.is_null() {
        return Err(win32_error("load OBS window cursor"));
    }
    let class = WindowClass {
        style: 0,
        window_proc: Some(window_proc),
        class_extra: 0,
        window_extra: 0,
        instance,
        icon: ptr::null_mut(),
        cursor,
        background: 6usize as *mut c_void, // COLOR_WINDOW + 1, not an owned brush.
        menu_name: ptr::null(),
        class_name: class_name.as_ptr(),
    };
    // SAFETY: all pointers remain live through UnregisterClassW; callback is static.
    if unsafe { RegisterClassW(&class) } == 0 {
        return Err(win32_error("register OBS window class"));
    }

    let mut window = ptr::null_mut();
    let running = (|| {
        // SAFETY: registered class and UTF-16 strings are live; null parent makes a
        // normal top-level window. All creation, dispatch and destruction stay here.
        window = unsafe {
            CreateWindowExW(
                WS_EX_APPWINDOW,
                class_name.as_ptr(),
                title.as_ptr(),
                WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_MINIMIZE,
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                560,
                230,
                ptr::null_mut(),
                ptr::null_mut(),
                instance,
                ptr::null_mut(),
            )
        };
        if window.is_null() {
            return Err(win32_error("create OBS capture window"));
        }
        // SAFETY: built-in STATIC class copies its live, terminated text; its
        // lifetime is owned by the parent HWND and ends with DestroyWindow.
        let text = unsafe {
            CreateWindowExW(
                0,
                static_class.as_ptr(),
                instructions.as_ptr(),
                WS_CHILD | WS_VISIBLE,
                20,
                18,
                510,
                165,
                window,
                ptr::null_mut(),
                instance,
                ptr::null_mut(),
            )
        };
        if text.is_null() {
            return Err(win32_error("create OBS window instructions"));
        }
        // SAFETY: this thread owns HWND. Showing an already minimized window via
        // SetWindowPos avoids first-ShowWindow STARTUPINFO overrides and activation.
        if unsafe {
            SetWindowPos(
                window,
                ptr::null_mut(),
                0,
                0,
                0,
                0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW,
            )
        } == 0
        {
            return Err(win32_error("show minimized OBS capture window"));
        }
        ready.send(window as usize).map_err(|_| {
            io::Error::new(
                io::ErrorKind::BrokenPipe,
                "OBS window owner left during startup",
            )
        })?;
        message_loop(stop_event)
    })();

    let destroyed = if window.is_null() {
        Ok(())
    } else {
        // SAFETY: HWND was created here and never transferred to another owner.
        if unsafe { DestroyWindow(window) } == 0 {
            Err(win32_error("destroy OBS capture window"))
        } else {
            Ok(())
        }
    };
    // SAFETY: same module/name as registration, after child and parent teardown.
    let unregistered = if unsafe { UnregisterClassW(class_name.as_ptr(), instance) } == 0 {
        Err(win32_error("unregister OBS window class"))
    } else {
        Ok(())
    };
    merge(merge(running, destroyed), unregistered)
}

fn message_loop(stop_event: usize) -> io::Result<()> {
    let stop_event = stop_event as *mut c_void;
    loop {
        // SAFETY: owner retains the event until join; pointer references one handle.
        // INPUTAVAILABLE lets us drain one message per iteration without starving stop.
        let wake = unsafe {
            MsgWaitForMultipleObjectsEx(1, &stop_event, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE)
        };
        match wake {
            WAIT_OBJECT_0 => return Ok(()),
            value if value == WAIT_OBJECT_0 + 1 => {
                let mut message = Message::default();
                // SAFETY: initialized MSG storage, current thread's queue, no filters.
                if unsafe { PeekMessageW(&mut message, ptr::null_mut(), 0, 0, PM_REMOVE) } != 0 {
                    if message.message == WM_QUIT {
                        return Ok(());
                    }
                    // SAFETY: message came from this thread's queue and our class
                    // callback cannot unwind or access borrowed Rust state.
                    unsafe {
                        TranslateMessage(&message);
                        DispatchMessageW(&message);
                    }
                }
            }
            WAIT_FAILED => return Err(win32_error("wait for OBS window messages")),
            value => {
                return Err(io::Error::other(format!(
                    "unexpected OBS window wait status {value}"
                )))
            }
        }
    }
}

unsafe extern "system" fn window_proc(
    window: *mut c_void,
    message: u32,
    wparam: usize,
    lparam: isize,
) -> isize {
    match message {
        WM_CLOSE => {
            // SAFETY: Windows invokes this callback for our live HWND on its owner
            // thread. ShowWindow returns prior visibility, not a success status.
            unsafe { ShowWindow(window, SW_SHOWMINNOACTIVE) };
            0
        }
        WM_DESTROY => {
            // SAFETY: posts to this GUI thread only; no borrowed state is accessed.
            unsafe { PostQuitMessage(0) };
            0
        }
        // SAFETY: forward the unmodified system-supplied window message.
        _ => unsafe { DefWindowProcW(window, message, wparam, lparam) },
    }
}

fn close_event(event: usize) -> io::Result<()> {
    // SAFETY: event is closed exactly once, after thread exit or failed spawn.
    if unsafe { CloseHandle(event as *mut c_void) } == 0 {
        Err(win32_error("close OBS window shutdown event"))
    } else {
        Ok(())
    }
}

fn win32_error(operation: &str) -> io::Error {
    let error = io::Error::last_os_error();
    io::Error::new(error.kind(), format!("{operation}: {error}"))
}

fn merge<T>(operation: io::Result<T>, cleanup: io::Result<()>) -> io::Result<T> {
    match (operation, cleanup) {
        (operation, Ok(())) => operation,
        (Ok(_), Err(error)) => Err(error),
        (Err(error), Err(cleanup)) => Err(io::Error::new(
            error.kind(),
            format!("{error}; cleanup also failed: {cleanup}"),
        )),
    }
}

const WS_EX_APPWINDOW: u32 = 0x0004_0000;
const WS_CAPTION: u32 = 0x00c0_0000;
const WS_SYSMENU: u32 = 0x0008_0000;
const WS_MINIMIZEBOX: u32 = 0x0002_0000;
const WS_MINIMIZE: u32 = 0x2000_0000;
const WS_CHILD: u32 = 0x4000_0000;
const WS_VISIBLE: u32 = 0x1000_0000;
const CW_USEDEFAULT: i32 = i32::MIN;
const SWP_NOSIZE: u32 = 0x0001;
const SWP_NOMOVE: u32 = 0x0002;
const SWP_NOZORDER: u32 = 0x0004;
const SWP_NOACTIVATE: u32 = 0x0010;
const SWP_SHOWWINDOW: u32 = 0x0040;
const SW_SHOWMINNOACTIVE: i32 = 7;
const WM_CLOSE: u32 = 0x0010;
const WM_QUIT: u32 = 0x0012;
const WM_DESTROY: u32 = 0x0002;
const PM_REMOVE: u32 = 0x0001;
const QS_ALLINPUT: u32 = 0x04ff;
const MWMO_INPUTAVAILABLE: u32 = 0x0004;
const WAIT_OBJECT_0: u32 = 0;
const WAIT_FAILED: u32 = 0xffff_ffff;
const INFINITE: u32 = 0xffff_ffff;

#[repr(C)]
struct WindowClass {
    style: u32,
    window_proc: Option<unsafe extern "system" fn(*mut c_void, u32, usize, isize) -> isize>,
    class_extra: i32,
    window_extra: i32,
    instance: *mut c_void,
    icon: *mut c_void,
    cursor: *mut c_void,
    background: *mut c_void,
    menu_name: *const u16,
    class_name: *const u16,
}

#[repr(C)]
#[derive(Default)]
struct Point {
    x: i32,
    y: i32,
}

#[repr(C)]
#[derive(Default)]
struct Message {
    window: *mut c_void,
    message: u32,
    wparam: usize,
    lparam: isize,
    time: u32,
    point: Point,
    private: u32,
}

#[link(name = "kernel32")]
extern "system" {
    fn GetModuleHandleW(module_name: *const u16) -> *mut c_void;
    fn CreateEventW(
        attributes: *const c_void,
        manual_reset: i32,
        initial_state: i32,
        name: *const u16,
    ) -> *mut c_void;
    fn SetEvent(event: *mut c_void) -> i32;
    fn CloseHandle(handle: *mut c_void) -> i32;
}

#[link(name = "user32")]
extern "system" {
    fn LoadCursorW(instance: *mut c_void, cursor_name: *const u16) -> *mut c_void;
    fn RegisterClassW(class: *const WindowClass) -> u16;
    fn UnregisterClassW(class_name: *const u16, instance: *mut c_void) -> i32;
    fn CreateWindowExW(
        ex_style: u32,
        class_name: *const u16,
        window_name: *const u16,
        style: u32,
        x: i32,
        y: i32,
        width: i32,
        height: i32,
        parent: *mut c_void,
        menu: *mut c_void,
        instance: *mut c_void,
        param: *mut c_void,
    ) -> *mut c_void;
    fn SetWindowPos(
        window: *mut c_void,
        insert_after: *mut c_void,
        x: i32,
        y: i32,
        width: i32,
        height: i32,
        flags: u32,
    ) -> i32;
    fn DestroyWindow(window: *mut c_void) -> i32;
    fn ShowWindow(window: *mut c_void, command: i32) -> i32;
    fn DefWindowProcW(window: *mut c_void, message: u32, wparam: usize, lparam: isize) -> isize;
    fn PostQuitMessage(exit_code: i32);
    fn MsgWaitForMultipleObjectsEx(
        count: u32,
        handles: *const *mut c_void,
        milliseconds: u32,
        wake_mask: u32,
        flags: u32,
    ) -> u32;
    fn PeekMessageW(
        message: *mut Message,
        window: *mut c_void,
        min: u32,
        max: u32,
        remove: u32,
    ) -> i32;
    fn TranslateMessage(message: *const Message) -> i32;
    fn DispatchMessageW(message: *const Message) -> isize;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[link(name = "user32")]
    extern "system" {
        fn IsWindow(window: *mut c_void) -> i32;
        fn GetWindowLongPtrW(window: *mut c_void, index: i32) -> isize;
        fn IsIconic(window: *mut c_void) -> i32;
        fn SendMessageTimeoutW(
            window: *mut c_void,
            message: u32,
            wparam: usize,
            lparam: isize,
            flags: u32,
            timeout: u32,
            result: *mut usize,
        ) -> isize;
    }

    #[test]
    fn window_ownership_covers_enable_close_disable_and_drop() {
        let mut capture = ObsCaptureWindow::new();
        assert!(capture.gui.is_none());
        capture
            .set_enabled(false)
            .expect("disable while already off");
        capture
            .set_enabled(true)
            .expect("create minimized OBS window");
        let window = capture.gui.as_ref().expect("GUI owner").window as *mut c_void;
        // Service window stations hide every HWND, even with WS_VISIBLE set.
        // SAFETY: GUI ownership keeps HWND live throughout these observations.
        unsafe {
            assert_ne!(IsWindow(window), 0);
            assert_ne!(GetWindowLongPtrW(window, -16) & WS_VISIBLE as isize, 0);
            assert_ne!(IsIconic(window), 0);
        }
        capture.set_enabled(true).expect("idempotent enable");
        assert_eq!(
            capture.gui.as_ref().expect("same GUI owner").window,
            window as usize
        );
        let mut response = 0;
        // SAFETY: WM_SYSCOMMAND/SC_RESTORE has scalar arguments; the bounded
        // synchronous send completes restoration before the visibility check.
        assert_ne!(
            unsafe { SendMessageTimeoutW(window, 0x0112, 0xf120, 0, 0x0002, 2000, &mut response) },
            0
        );
        // SAFETY: capture retains the live HWND until explicit disable.
        assert_eq!(unsafe { IsIconic(window) }, 0);
        // SAFETY: scalar WM_CLOSE has no borrowed payload; output storage remains
        // live through the synchronous, two-second-bounded message dispatch.
        assert_ne!(
            unsafe { SendMessageTimeoutW(window, WM_CLOSE, 0, 0, 0x0002, 2000, &mut response) },
            0
        );
        // SAFETY: closing must preserve the same owned window in minimized state.
        unsafe {
            assert_ne!(IsWindow(window), 0);
            assert_ne!(IsIconic(window), 0);
        }
        capture.set_enabled(false).expect("stop and join OBS GUI");
        assert!(capture.gui.is_none());
        // SAFETY: IsWindow accepts a stale handle for an existence check.
        assert_eq!(unsafe { IsWindow(window) }, 0);
        capture.set_enabled(false).expect("idempotent disable");
        capture
            .set_enabled(true)
            .expect("re-register window after disable");
        let window = capture.gui.as_ref().expect("new GUI owner").window as *mut c_void;
        drop(capture);
        // SAFETY: IsWindow accepts a stale handle for an existence check.
        assert_eq!(unsafe { IsWindow(window) }, 0);
    }
}
