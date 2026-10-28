use super::{parse_status, AudioFrame, Shared, MAX_STEMS};
use crate::gamestate::GameState;
use crossbeam_channel::{Receiver, Sender};
use std::ffi::c_void;
use std::io;
use std::mem::size_of;
use std::ptr;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

type Handle = *mut c_void;
#[repr(C)]
struct SecurityAttributes {
    length: u32,
    descriptor: *mut c_void,
    inherit: i32,
}
#[repr(C)]
struct SidAndAttributes {
    sid: *mut c_void,
    attributes: u32,
}
struct OwnedHandle(Handle);
impl Drop for OwnedHandle {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.0);
        }
    }
}
struct LocalMemory(*mut c_void);
impl Drop for LocalMemory {
    fn drop(&mut self) {
        unsafe {
            LocalFree(self.0);
        }
    }
}

fn last_error() -> io::Error {
    io::Error::last_os_error()
}

pub(crate) fn qpc_ns() -> u64 {
    // SAFETY: both QPC APIs write one i64; frequency is fixed for the boot session.
    unsafe {
        let mut counter = 0i64;
        let mut frequency = 0i64;
        if QueryPerformanceCounter(&mut counter) == 0
            || QueryPerformanceFrequency(&mut frequency) == 0
            || frequency <= 0
        {
            return 0;
        }
        ((counter as u128 * 1_000_000_000u128) / frequency as u128) as u64
    }
}

fn user_sid() -> io::Result<String> {
    // SAFETY: token query buffer is aligned, bounded by the kernel size, and retained while SID is read.
    unsafe {
        let mut token = ptr::null_mut();
        if OpenProcessToken(GetCurrentProcess(), 8, &mut token) == 0 {
            return Err(last_error());
        }
        let token = OwnedHandle(token);
        let mut length = 0;
        GetTokenInformation(token.0, 1, ptr::null_mut(), 0, &mut length);
        if length == 0 || length > 65536 {
            return Err(io::Error::other("invalid user token size"));
        }
        let mut buffer = vec![0usize; (length as usize).div_ceil(size_of::<usize>())];
        if GetTokenInformation(token.0, 1, buffer.as_mut_ptr().cast(), length, &mut length) == 0 {
            return Err(last_error());
        }
        let user = &*(buffer.as_ptr().cast::<SidAndAttributes>());
        let mut text = ptr::null_mut();
        if ConvertSidToStringSidW(user.sid, &mut text) == 0 {
            return Err(last_error());
        }
        let memory = LocalMemory(text.cast());
        let mut length = 0;
        while length < 256 && *text.add(length) != 0 {
            length += 1;
        }
        if length == 256 {
            return Err(io::Error::other("invalid user SID"));
        }
        let sid = String::from_utf16(std::slice::from_raw_parts(text, length))
            .map_err(|_| io::Error::other("invalid user SID"))?;
        drop(memory);
        Ok(sid)
    }
}

fn create_pipe(prefix: &str) -> io::Result<OwnedHandle> {
    let sid = user_sid()?;
    let name: Vec<u16> = format!("\\\\.\\pipe\\{prefix}.{sid}\0")
        .encode_utf16()
        .collect();
    let dacl: Vec<u16> = format!("D:P(A;;GA;;;SY)(A;;GA;;;{sid})\0")
        .encode_utf16()
        .collect();
    // SAFETY: descriptor and UTF-16 strings remain live through CreateNamedPipeW.
    unsafe {
        let mut descriptor = ptr::null_mut();
        if ConvertStringSecurityDescriptorToSecurityDescriptorW(
            dacl.as_ptr(),
            1,
            &mut descriptor,
            ptr::null_mut(),
        ) == 0
        {
            return Err(last_error());
        }
        let memory = LocalMemory(descriptor);
        let attributes = SecurityAttributes {
            length: size_of::<SecurityAttributes>() as u32,
            descriptor,
            inherit: 0,
        };
        // Nonblocking byte mode means there are no pending kernel operations to cancel at retirement.
        let handle = CreateNamedPipeW(
            name.as_ptr(),
            3 | 0x00080000,
            1 | 8,
            1,
            262144,
            4096,
            0,
            &attributes,
        );
        drop(memory);
        if handle as isize == -1 {
            return Err(last_error());
        }
        Ok(OwnedHandle(handle))
    }
}

fn write_packet(pipe: Handle, bytes: &[u8], shared: &Shared, stop: &AtomicBool) -> io::Result<()> {
    let deadline = Instant::now() + Duration::from_millis(250);
    let mut offset = 0;
    while offset < bytes.len() {
        if stop.load(Ordering::Acquire) {
            return Err(io::Error::new(io::ErrorKind::Interrupted, "bridge stopped"));
        }
        if shared.failed.load(Ordering::Acquire) {
            return Err(io::Error::other("audio queue overflow"));
        }
        let mut written = 0;
        // SAFETY: pipe is owned by this worker; slice remains live throughout this synchronous, nonblocking call.
        if unsafe {
            WriteFile(
                pipe,
                bytes[offset..].as_ptr().cast(),
                (bytes.len() - offset) as u32,
                &mut written,
                ptr::null_mut(),
            )
        } == 0
        {
            return Err(last_error());
        }
        offset += written as usize;
        if Instant::now() >= deadline {
            return Err(io::Error::new(
                io::ErrorKind::TimedOut,
                "OBS client is not consuming audio",
            ));
        }
        if written == 0 {
            std::thread::sleep(Duration::from_millis(2));
        }
    }
    Ok(())
}

fn hello(mode: u8) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(16);
    bytes.extend_from_slice(&12u32.to_le_bytes());
    bytes.push(1);
    bytes.extend_from_slice(&1u32.to_le_bytes());
    bytes.push(mode);
    bytes.extend_from_slice(&48000u32.to_le_bytes());
    bytes.extend_from_slice(&2u16.to_le_bytes());
    bytes
}

fn encode_audio(frame: &AudioFrame, track: u32, bytes: &mut Vec<u8>) {
    bytes.clear();
    let length = 1 + 8 + 4 + 1 + 2 + frame.name_len + 2 + frame.samples.len() * 4;
    bytes.extend_from_slice(&(length as u32).to_le_bytes());
    bytes.push(2);
    bytes.extend_from_slice(&frame.timestamp.to_le_bytes());
    bytes.extend_from_slice(&track.to_le_bytes());
    bytes.push(frame.group);
    bytes.extend_from_slice(&(frame.name_len as u16).to_le_bytes());
    bytes.extend_from_slice(&frame.name[..frame.name_len]);
    bytes.extend_from_slice(&960u16.to_le_bytes());
    for sample in frame.samples {
        bytes.extend_from_slice(&sample.to_le_bytes());
    }
}

// Identity tokens are never reused within a helper session, so they key plugin tracks directly.
// Track 0 is the combined mix. A real u32::MAX token may share the private track; the plugin
// files private audio by group, so that only shares its timestamp ordering.
const PRIVATE_TRACK: u32 = u32::MAX;

fn track(frame: &AudioFrame) -> u32 {
    if frame.combined {
        0
    } else if frame.identity == 0 {
        PRIVATE_TRACK
    } else {
        frame.identity
    }
}

const WAITING: &str = "Waiting for OBS Perfect Comms source";

// ERROR_BROKEN_PIPE, ERROR_NO_DATA (closing pipe) and ERROR_PIPE_LISTENING all mean the client left.
fn client_gone(error: &io::Error) -> bool {
    matches!(error.raw_os_error(), Some(109 | 232 | 536))
}

pub(super) fn serve(
    shared: &Shared,
    stop: &AtomicBool,
    gs: &GameState,
    pending: &Receiver<Box<AudioFrame>>,
    free: &Sender<Box<AudioFrame>>,
) -> io::Result<()> {
    serve_named("PerfectComms.OBS", shared, stop, gs, pending, free)
}

fn serve_named(
    prefix: &str,
    shared: &Shared,
    stop: &AtomicBool,
    gs: &GameState,
    pending: &Receiver<Box<AudioFrame>>,
    free: &Sender<Box<AudioFrame>>,
) -> io::Result<()> {
    let pipe = create_pipe(prefix)?;
    let mut bytes = Vec::with_capacity(8192);
    let mut input = Vec::with_capacity(1032);
    shared.status("waiting", WAITING);
    while !stop.load(Ordering::Acquire) {
        loop {
            if stop.load(Ordering::Acquire) {
                return Ok(());
            }
            // SAFETY: exclusively owned nonblocking server pipe. A nonzero return only means the
            // instance is listening again after a previous client.
            if unsafe { ConnectNamedPipe(pipe.0, ptr::null_mut()) } == 0 {
                let error = last_error();
                match error.raw_os_error() {
                    Some(535) => break,
                    Some(536) => {}
                    // SAFETY: the client already closed; disconnecting resets the owned instance.
                    Some(232) => unsafe {
                        DisconnectNamedPipe(pipe.0);
                    },
                    _ => return Err(error),
                }
            }
            std::thread::sleep(Duration::from_millis(5));
        }
        while let Ok(frame) = pending.try_recv() {
            let _ = free.try_send(frame);
        }
        shared.failed.store(false, Ordering::Release);
        let mut mode = shared.mode.load(Ordering::Acquire);
        if let Err(error) = write_packet(pipe.0, &hello(mode), shared, stop) {
            unsafe {
                DisconnectNamedPipe(pipe.0);
            }
            if stop.load(Ordering::Acquire) {
                return Ok(());
            }
            if client_gone(&error) {
                shared.status("waiting", WAITING);
            } else {
                shared.status("error", format!("OBS handshake failed: {error}"));
            }
            continue;
        }
        let generation = shared.begin_connection();
        input.clear();
        shared.status("ready", "OBS connected");
        let connection = (|| -> io::Result<()> {
            while !stop.load(Ordering::Acquire) {
                if shared.failed.load(Ordering::Acquire) {
                    return Err(io::Error::other(
                        "audio queue overflow; reconnect OBS source",
                    ));
                }
                let next_mode = shared.mode.load(Ordering::Acquire);
                if next_mode != mode {
                    write_packet(pipe.0, &hello(next_mode), shared, stop)?;
                    mode = next_mode;
                }
                let mut incoming = [0u8; 1032];
                let mut count = 0;
                // SAFETY: bounded writable buffer, synchronous nonblocking operation.
                let read = unsafe {
                    ReadFile(
                        pipe.0,
                        incoming.as_mut_ptr().cast(),
                        incoming.len() as u32,
                        &mut count,
                        ptr::null_mut(),
                    )
                };
                if read == 0 {
                    let error = last_error();
                    if error.raw_os_error() != Some(232) {
                        return Err(error);
                    }
                } else if count > 0 {
                    if input.len() + count as usize > 2064 {
                        return Err(io::Error::other("oversize OBS status"));
                    }
                    input.extend_from_slice(&incoming[..count as usize]);
                    while input.len() >= 4 {
                        let length = u32::from_le_bytes(input[..4].try_into().unwrap()) as usize;
                        if !(4..=1028).contains(&length) {
                            return Err(io::Error::other("invalid OBS status length"));
                        }
                        if input.len() < length + 4 {
                            break;
                        }
                        let (state, text) = parse_status(&input[4..length + 4])?;
                        shared.status(state, text);
                        input.drain(..length + 4);
                    }
                }
                for _ in 0..MAX_STEMS + 1 {
                    let Ok(frame) = pending.try_recv() else {
                        break;
                    };
                    let snapshot = gs.snapshot();
                    let authorized = shared.frame_authorized(&frame, &snapshot, generation);
                    let sent = (|| -> io::Result<()> {
                        if !authorized {
                            return Ok(());
                        }
                        encode_audio(&frame, track(&frame), &mut bytes);
                        write_packet(pipe.0, &bytes, shared, stop)
                    })();
                    free.try_send(frame)
                        .map_err(|_| io::Error::other("invalid audio frame pool"))?;
                    sent?;
                }
                std::thread::sleep(Duration::from_millis(2));
            }
            Ok(())
        })();
        shared.cancel();
        // SAFETY: no outstanding I/O exists in PIPE_NOWAIT mode.
        unsafe {
            DisconnectNamedPipe(pipe.0);
        }
        match connection {
            Err(_) if stop.load(Ordering::Acquire) => {}
            Err(error) if client_gone(&error) => shared.status("waiting", WAITING),
            Err(error) => shared.status(
                "error",
                format!("OBS recording bridge disconnected: {error}"),
            ),
            Ok(()) => {}
        }
    }
    Ok(())
}

#[link(name = "kernel32")]
extern "system" {
    fn GetCurrentProcess() -> Handle;
    fn CloseHandle(handle: Handle) -> i32;
    fn LocalFree(memory: *mut c_void) -> *mut c_void;
    fn QueryPerformanceCounter(value: *mut i64) -> i32;
    fn QueryPerformanceFrequency(value: *mut i64) -> i32;
    fn CreateNamedPipeW(
        name: *const u16,
        access: u32,
        mode: u32,
        instances: u32,
        output: u32,
        input: u32,
        timeout: u32,
        attributes: *const SecurityAttributes,
    ) -> Handle;
    fn ConnectNamedPipe(pipe: Handle, overlapped: *mut c_void) -> i32;
    fn DisconnectNamedPipe(pipe: Handle) -> i32;
    fn ReadFile(
        file: Handle,
        buffer: *mut c_void,
        length: u32,
        read: *mut u32,
        overlapped: *mut c_void,
    ) -> i32;
    fn WriteFile(
        file: Handle,
        buffer: *const c_void,
        length: u32,
        written: *mut u32,
        overlapped: *mut c_void,
    ) -> i32;
}
#[link(name = "advapi32")]
extern "system" {
    fn OpenProcessToken(process: Handle, access: u32, token: *mut Handle) -> i32;
    fn GetTokenInformation(
        token: Handle,
        class: u32,
        buffer: *mut c_void,
        length: u32,
        needed: *mut u32,
    ) -> i32;
    fn ConvertSidToStringSidW(sid: *mut c_void, text: *mut *mut u16) -> i32;
    fn ConvertStringSecurityDescriptorToSecurityDescriptorW(
        text: *const u16,
        revision: u32,
        descriptor: *mut *mut c_void,
        length: *mut u32,
    ) -> i32;
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn hello_and_audio_follow_plugin_framing() {
        assert_eq!(
            hello(1),
            [12, 0, 0, 0, 1, 1, 0, 0, 0, 1, 128, 187, 0, 0, 2, 0]
        );
        let mut frame = AudioFrame {
            timestamp: 123,
            ..AudioFrame::default()
        };
        frame.samples[0] = 0.25;
        let mut bytes = Vec::new();
        encode_audio(&frame, 7, &mut bytes);
        assert_eq!(
            u32::from_le_bytes(bytes[..4].try_into().unwrap()) as usize,
            bytes.len() - 4
        );
        assert_eq!(bytes[4], 2);
        assert_eq!(u32::from_le_bytes(bytes[13..17].try_into().unwrap()), 7);
        assert_eq!(u16::from_le_bytes(bytes[20..22].try_into().unwrap()), 960);
        assert_eq!(f32::from_le_bytes(bytes[22..26].try_into().unwrap()), 0.25);
    }
}

#[cfg(test)]
mod pipe_tests {
    use super::*;
    use crate::gamestate::{LocalState, PeerState, RecordingIdentity};
    use crate::obs_recording::SAMPLES;
    use std::fs::{File, OpenOptions};
    use std::io::Read;
    use std::sync::Arc;
    use std::thread::JoinHandle;

    const TIMEOUT: Duration = Duration::from_secs(10);

    struct Server {
        shared: Arc<Shared>,
        stop: Arc<AtomicBool>,
        thread: Option<JoinHandle<io::Result<()>>>,
        path: String,
    }

    impl Server {
        fn start(name: &str, gs: Arc<GameState>) -> Self {
            let shared = Arc::new(Shared::new());
            shared.initialize_pool().unwrap();
            let stop = Arc::new(AtomicBool::new(false));
            let prefix = format!("PerfectComms.OBS.test.{}.{name}", std::process::id());
            let path = format!(r"\\.\pipe\{prefix}.{}", user_sid().unwrap());
            let pending = shared.publisher.queued.clone();
            let free = shared.publisher.recycle.clone();
            let thread = {
                let (shared, stop) = (shared.clone(), stop.clone());
                std::thread::spawn(move || {
                    serve_named(&prefix, &shared, &stop, &gs, &pending, &free)
                })
            };
            Self {
                shared,
                stop,
                thread: Some(thread),
                path,
            }
        }

        fn connect(&self) -> File {
            let deadline = Instant::now() + TIMEOUT;
            loop {
                match OpenOptions::new().read(true).write(true).open(&self.path) {
                    Ok(file) => return file,
                    Err(error) => assert!(Instant::now() < deadline, "connect: {error}"),
                }
                std::thread::sleep(Duration::from_millis(5));
            }
        }

        fn next_status(&self) -> (&'static str, String) {
            let deadline = Instant::now() + TIMEOUT;
            loop {
                if let Some(status) = self.shared.take_status() {
                    return status;
                }
                assert!(Instant::now() < deadline, "no bridge status");
                std::thread::sleep(Duration::from_millis(2));
            }
        }

        fn wait_ready(&self) {
            loop {
                let (state, text) = self.next_status();
                assert_ne!(state, "error", "{text}");
                if state == "ready" {
                    return;
                }
            }
        }

        fn finish(mut self) -> io::Result<()> {
            self.stop.store(true, Ordering::Release);
            self.thread.take().unwrap().join().unwrap()
        }
    }

    impl Drop for Server {
        fn drop(&mut self) {
            self.stop.store(true, Ordering::Release);
            if let Some(thread) = self.thread.take() {
                let _ = thread.join();
            }
        }
    }

    #[test]
    fn client_close_returns_to_waiting_and_accepts_reconnect() {
        let server = Server::start("reconnect", Arc::new(GameState::new()));
        let client = server.connect();
        server.wait_ready();
        drop(client);
        assert_eq!(server.next_status().0, "waiting");
        drop(server.connect());
        let mut client = server.connect();
        server.wait_ready();
        let mut greeting = [0u8; 16];
        client.read_exact(&mut greeting).unwrap();
        assert_eq!(greeting[..], hello(0)[..]);
        drop(client);
        server.finish().unwrap();
    }

    #[test]
    fn identity_tokens_are_tracks_without_a_connection_cap() {
        const TOKENS: u32 = 300;
        let gs = Arc::new(GameState::new());
        gs.apply(
            LocalState::default(),
            1.0,
            vec![(
                "peer".into(),
                PeerState {
                    gain: 1.0,
                    pan: 0.0,
                    mode: 0,
                    muffled: false,
                },
            )],
        );
        let server = Server::start("tokens", gs.clone());
        let mut client = server.connect();
        server.wait_ready();
        let (sender, received) = crossbeam_channel::bounded(1);
        std::thread::spawn(move || {
            let mut tracks = Vec::new();
            let mut packet = Vec::new();
            let result = (|| -> io::Result<()> {
                while tracks.len() < TOKENS as usize {
                    let mut length = [0u8; 4];
                    client.read_exact(&mut length)?;
                    packet.resize(u32::from_le_bytes(length) as usize, 0);
                    client.read_exact(&mut packet)?;
                    if packet[0] == 2 {
                        tracks.push(u32::from_le_bytes(packet[9..13].try_into().unwrap()));
                    }
                }
                Ok(())
            })();
            let _ = sender.send(result.map(|()| (tracks, client)));
        });
        let connection = server.shared.connected.load(Ordering::Acquire);
        let route = server.shared.route_epoch.load(Ordering::Acquire);
        server.shared.set_audible(true);
        let snapshot = gs.snapshot();
        let peer_epoch = snapshot.recording_peer_epochs["peer"];
        for token in 1..=TOKENS {
            while server.shared.publisher.free.is_empty() {
                std::thread::sleep(Duration::from_millis(1));
            }
            let identity = RecordingIdentity {
                id: token,
                name: "Alias".into(),
                group: 1,
            };
            server.shared.publish(
                &snapshot,
                u64::from(token),
                Some((&identity, peer_epoch)),
                &[0.0; SAMPLES],
                connection,
                route,
            );
        }
        let (tracks, client) = received.recv_timeout(TIMEOUT).unwrap().unwrap();
        assert_eq!(tracks, (1..=TOKENS).collect::<Vec<_>>());
        assert!(server.shared.take_status().is_none());
        drop(client);
        server.finish().unwrap();
    }

    #[test]
    fn private_and_combined_frames_keep_reserved_tracks() {
        let mut frame = AudioFrame {
            combined: true,
            ..AudioFrame::default()
        };
        assert_eq!(track(&frame), 0);
        frame.combined = false;
        assert_eq!(track(&frame), PRIVATE_TRACK);
        frame.identity = 42;
        assert_eq!(track(&frame), 42);
    }
}
