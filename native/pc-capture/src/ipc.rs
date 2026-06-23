use crate::proto;
use std::io::{BufRead, Write};
use std::net::{TcpListener, TcpStream};
use std::path::{Path, PathBuf};

pub const MAX_FRAME_LEN: usize = proto::FRAME_BYTES * 4;

pub struct ServerConfig {
    pub handshake_path: PathBuf,
    pub token: String,
    pub synthetic: bool,
}

pub fn bind_loopback() -> std::io::Result<TcpListener> {
    TcpListener::bind(("127.0.0.1", 0))
}

pub fn write_handshake_file(path: &Path, port: u16, pid: u32) -> std::io::Result<()> {
    let body = format!("{{\"port\":{port},\"pid\":{pid}}}");
    let temp = path.with_extension(format!("tmp.{pid}"));
    {
        let mut f = std::fs::File::create(&temp)?;
        f.write_all(body.as_bytes())?;
        f.flush()?;
    }
    std::fs::rename(&temp, path)?;
    Ok(())
}

pub fn accept_single(listener: &TcpListener) -> std::io::Result<TcpStream> {
    let (stream, _addr) = listener.accept()?;
    stream.set_nodelay(true).ok();
    Ok(stream)
}

pub fn reject_extra_client(stream: &mut TcpStream) -> std::io::Result<()> {
    let body = proto::encode_control(&proto::error_json("busy", "single client only"));
    stream.write_all(&body).ok();
    stream.shutdown(std::net::Shutdown::Both)
}

pub fn read_token_line<R: BufRead>(r: &mut R) -> std::io::Result<String> {
    let mut line = String::new();
    r.read_line(&mut line)?;
    Ok(line.trim_end_matches(['\r', '\n']).to_string())
}

pub fn check_frame_len(len: usize) -> Result<(), proto::DecodeError> {
    if len > MAX_FRAME_LEN {
        return Err(proto::DecodeError::BadLen(len));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::BufReader;

    #[test]
    fn bind_is_loopback_with_ephemeral_port() {
        let l = bind_loopback().unwrap();
        let addr = l.local_addr().unwrap();
        assert!(addr.ip().is_loopback());
        assert_ne!(addr.port(), 0);
    }

    #[test]
    fn handshake_file_is_valid_json_with_port_and_pid() {
        let dir = std::env::temp_dir();
        let path = dir.join(format!("pc-capture-hs-{}.json", std::process::id()));
        write_handshake_file(&path, 54321, 9999).unwrap();
        let body = std::fs::read_to_string(&path).unwrap();
        let v: serde_json::Value = serde_json::from_str(&body).unwrap();
        assert_eq!(v["port"], 54321);
        assert_eq!(v["pid"], 9999);
        std::fs::remove_file(&path).ok();
    }

    #[test]
    fn read_token_line_trims_newline() {
        let mut r = BufReader::new(&b"my-secret-token\r\n"[..]);
        let tok = read_token_line(&mut r).unwrap();
        assert_eq!(tok, "my-secret-token");
    }

    #[test]
    fn accept_single_returns_first_client() {
        let l = bind_loopback().unwrap();
        let port = l.local_addr().unwrap().port();
        let h = std::thread::spawn(move || {
            let _c = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
            std::thread::sleep(std::time::Duration::from_millis(50));
        });
        let stream = accept_single(&l).unwrap();
        assert!(stream.peer_addr().unwrap().ip().is_loopback());
        h.join().unwrap();
    }

    #[test]
    fn second_connection_is_rejected_after_first_accepted() {
        let l = bind_loopback().unwrap();
        let port = l.local_addr().unwrap().port();
        let _first = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        let accepted = accept_single(&l).unwrap();
        assert!(accepted.peer_addr().unwrap().ip().is_loopback());

        let _second = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        let mut rejected = accept_single(&l).unwrap();
        reject_extra_client(&mut rejected).unwrap();
    }

    #[test]
    fn max_frame_len_guard_rejects_oversized_len() {
        assert!(check_frame_len(MAX_FRAME_LEN).is_ok());
        assert!(check_frame_len(MAX_FRAME_LEN + 1).is_err());
        assert!(check_frame_len(0).is_ok());
    }

    #[test]
    fn max_frame_len_covers_largest_legit_frame() {
        assert!(MAX_FRAME_LEN >= proto::FRAME_BYTES);
    }
}
