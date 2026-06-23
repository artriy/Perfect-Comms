mod audio;
mod ipc;
mod proto;

use ipc::ServerConfig;
use std::path::PathBuf;

#[derive(Debug)]
pub struct Args {
    pub handshake_path: Option<PathBuf>,
    pub synthetic: bool,
}

pub fn parse_args(argv: &[String]) -> Result<Args, String> {
    let mut handshake_path = None;
    let mut synthetic = false;
    let mut i = 1;
    while i < argv.len() {
        match argv[i].as_str() {
            "--handshake" => {
                i += 1;
                let p = argv.get(i).ok_or("--handshake requires a path")?;
                handshake_path = Some(PathBuf::from(p));
            }
            "--synthetic-tone" => synthetic = true,
            other => return Err(format!("unknown argument: {other}")),
        }
        i += 1;
    }
    if handshake_path.is_none() {
        return Err("--handshake <path> is required".to_string());
    }
    Ok(Args { handshake_path, synthetic })
}

fn main() {
    let argv: Vec<String> = std::env::args().collect();
    let args = match parse_args(&argv) {
        Ok(a) => a,
        Err(e) => {
            eprintln!("pc-capture: {e}");
            std::process::exit(2);
        }
    };

    let stdin = std::io::stdin();
    let mut locked = stdin.lock();
    let token = match ipc::read_token_line(&mut locked) {
        Ok(t) if !t.is_empty() => t,
        _ => {
            eprintln!("pc-capture: missing auth token on stdin");
            std::process::exit(2);
        }
    };

    let cfg = ServerConfig {
        handshake_path: args.handshake_path.unwrap(),
        token,
        synthetic: args.synthetic,
    };

    if let Err(e) = ipc::serve(cfg) {
        eprintln!("pc-capture: serve error: {e}");
        std::process::exit(1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_args_requires_handshake() {
        let err = parse_args(&["pc-capture".to_string()]).unwrap_err();
        assert!(err.contains("--handshake"), "got: {err}");
    }

    #[test]
    fn parse_args_reads_handshake_and_synthetic() {
        let argv = vec![
            "pc-capture".to_string(),
            "--handshake".to_string(),
            "/tmp/hs.json".to_string(),
            "--synthetic-tone".to_string(),
        ];
        let args = parse_args(&argv).unwrap();
        assert_eq!(args.handshake_path.unwrap().to_string_lossy(), "/tmp/hs.json");
        assert!(args.synthetic);
    }

    #[test]
    fn parse_args_handshake_without_synthetic_defaults_false() {
        let argv = vec![
            "pc-capture".to_string(),
            "--handshake".to_string(),
            "/tmp/hs.json".to_string(),
        ];
        let args = parse_args(&argv).unwrap();
        assert!(!args.synthetic);
    }
}
