use super::{parse_status, AudioFrame, Shared, MAX_STEMS, MAX_TRACKS};
use crate::gamestate::GameState;
use crossbeam_channel::{Receiver, Sender};
use std::collections::HashMap;
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

fn create_pipe() -> io::Result<OwnedHandle> {
    let sid = user_sid()?;
    let name: Vec<u16> = format!("\\\\.\\pipe\\PerfectComms.OBS.{sid}\0")
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

pub(super) fn serve(
    shared: &Shared,
    stop: &AtomicBool,
    gs: &GameState,
    pending: &Receiver<Box<AudioFrame>>,
    free: &Sender<Box<AudioFrame>>,
) -> io::Result<()> {
    let pipe = create_pipe()?;
    let mut bytes = Vec::with_capacity(8192);
    let mut input = Vec::with_capacity(1032);
    shared.status("waiting", "Waiting for OBS Perfect Comms source");
    while !stop.load(Ordering::Acquire) {
        loop {
            if stop.load(Ordering::Acquire) {
                return Ok(());
            }
            // SAFETY: exclusively owned nonblocking server pipe.
            let connected = unsafe { ConnectNamedPipe(pipe.0, ptr::null_mut()) } != 0;
            let error = if connected {
                535
            } else {
                last_error().raw_os_error().unwrap_or(0)
            };
            if error == 535 {
                break;
            }
            if error != 536 && error != 232 {
                return Err(io::Error::from_raw_os_error(error));
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
            shared.status("error", format!("OBS handshake failed: {error}"));
            continue;
        }
        let generation = shared.begin_connection();
        let mut tracks = HashMap::<u32, u32>::new();
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
                        let track = if frame.combined {
                            0
                        } else if let Some(track) = tracks.get(&frame.identity) {
                            *track
                        } else {
                            if tracks.len() >= MAX_TRACKS {
                                return Err(io::Error::other("too many recording identities"));
                            }
                            let track = tracks.len() as u32 + 1;
                            tracks.insert(frame.identity, track);
                            track
                        };
                        encode_audio(&frame, track, &mut bytes);
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
        if let Err(error) = connection {
            if !stop.load(Ordering::Acquire) {
                shared.status(
                    "error",
                    format!("OBS recording bridge disconnected: {error}"),
                );
            }
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
