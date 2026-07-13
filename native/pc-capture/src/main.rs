use pc_capture::ipc::ServerConfig;
use pc_capture::{audio, ipc, proto};
use std::path::PathBuf;

#[derive(Debug)]
pub struct Args {
    pub handshake_path: Option<PathBuf>,
    pub token_file: Option<PathBuf>,
    pub synthetic: bool,
    pub enumerate: bool,
    pub protocol_version: bool,
    pub owner_pid: Option<u32>,
}

pub fn parse_args(argv: &[String]) -> Result<Args, String> {
    let mut handshake_path = None;
    let mut token_file = None;
    let mut synthetic = false;
    let mut enumerate = false;
    let mut protocol_version = false;
    let mut owner_pid = None;
    let mut i = 1;
    while i < argv.len() {
        match argv[i].as_str() {
            "--handshake" => {
                i += 1;
                let p = argv.get(i).ok_or("--handshake requires a path")?;
                handshake_path = Some(PathBuf::from(p));
            }
            "--token-file" => {
                i += 1;
                let p = argv.get(i).ok_or("--token-file requires a path")?;
                token_file = Some(PathBuf::from(p));
            }
            "--synthetic-tone" => synthetic = true,
            "--enumerate" => enumerate = true,
            "--protocol-version" => protocol_version = true,
            "--owner-pid" => {
                i += 1;
                let value = argv.get(i).ok_or("--owner-pid requires a PID")?;
                let pid = value
                    .parse::<u32>()
                    .map_err(|_| "--owner-pid requires a positive integer PID")?;
                if pid == 0 {
                    return Err("--owner-pid requires a positive integer PID".to_string());
                }
                owner_pid = Some(pid);
            }
            other => return Err(format!("unknown argument: {other}")),
        }
        i += 1;
    }
    if handshake_path.is_none() && !protocol_version {
        return Err("--handshake <path> is required".to_string());
    }
    Ok(Args {
        handshake_path,
        token_file,
        synthetic,
        enumerate,
        protocol_version,
        owner_pid,
    })
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

    if args.protocol_version {
        println!("{}", proto::PROTO_VERSION);
        return;
    }

    if args.enumerate {
        let devices = audio::enumerate_devices();
        let output_devices = audio::enumerate_output_devices();
        let json = proto::devices_json(&devices, &output_devices);
        let path = args.handshake_path.as_ref().unwrap();
        if let Err(e) = ipc::write_devices_file(path, &json) {
            eprintln!("pc-capture: cannot write devices file: {e}");
            std::process::exit(1);
        }
        std::process::exit(0);
    }

    let token = match &args.token_file {
        Some(path) => match std::fs::read_to_string(path) {
            Ok(s) => s.trim_end_matches(['\r', '\n']).to_string(),
            Err(e) => {
                eprintln!("pc-capture: cannot read token file: {e}");
                std::process::exit(2);
            }
        },
        None => {
            let stdin = std::io::stdin();
            let mut locked = stdin.lock();
            ipc::read_token_line(&mut locked).unwrap_or_default()
        }
    };
    if token.is_empty() {
        eprintln!("pc-capture: missing auth token");
        std::process::exit(2);
    }

    let cfg = ServerConfig {
        handshake_path: args.handshake_path.unwrap(),
        token,
        synthetic: args.synthetic,
        owner_pid: args.owner_pid,
        hard_exit_on_disconnect: true,
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
        assert_eq!(
            args.handshake_path.unwrap().to_string_lossy(),
            "/tmp/hs.json"
        );
        assert!(args.synthetic);
    }

    #[test]
    fn protocol_version_does_not_require_handshake() {
        let args =
            parse_args(&["pc-capture".to_string(), "--protocol-version".to_string()]).unwrap();
        assert!(args.protocol_version);
        assert!(args.handshake_path.is_none());
    }

    #[test]
    fn parse_args_reads_owner_pid() {
        let args = parse_args(&[
            "pc-capture".to_string(),
            "--handshake".to_string(),
            "/tmp/hs.json".to_string(),
            "--owner-pid".to_string(),
            "4242".to_string(),
        ])
        .unwrap();
        assert_eq!(args.owner_pid, Some(4242));
    }

    #[test]
    fn parse_args_rejects_invalid_owner_pid() {
        for value in ["0", "not-a-pid"] {
            let error = parse_args(&[
                "pc-capture".to_string(),
                "--handshake".to_string(),
                "/tmp/hs.json".to_string(),
                "--owner-pid".to_string(),
                value.to_string(),
            ])
            .unwrap_err();
            assert!(error.contains("positive integer PID"), "got: {error}");
        }
    }

    #[test]
    fn parse_args_reads_enumerate_flag() {
        let argv = vec![
            "pc-capture".to_string(),
            "--handshake".to_string(),
            "/tmp/devs.json".to_string(),
            "--enumerate".to_string(),
        ];
        let args = parse_args(&argv).unwrap();
        assert!(args.enumerate);
        assert!(!args.synthetic);
    }

    #[test]
    fn parse_args_reads_token_file() {
        let argv = vec![
            "pc-capture".to_string(),
            "--handshake".to_string(),
            "/tmp/hs.json".to_string(),
            "--token-file".to_string(),
            "/tmp/tok".to_string(),
        ];
        let args = parse_args(&argv).unwrap();
        assert_eq!(args.token_file.unwrap().to_string_lossy(), "/tmp/tok");
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
