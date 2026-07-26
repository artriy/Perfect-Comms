use pc_capture::build_info;
use serde::Deserialize;
use std::fs::{self, OpenOptions};
use std::io::{Read, Write};
use std::os::unix::fs::{MetadataExt, OpenOptionsExt, PermissionsExt};
use std::os::unix::process::ExitStatusExt;
use std::path::{Component, Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::Arc;
use std::thread;
use std::time::{Duration, Instant};

const REQUEST_DIRECTORY_NAME: &str = ".perfect-comms-broker-v1";
const REQUEST_SCHEMA: u32 = 1;
const MAX_REQUEST_BYTES: u64 = 64 * 1024;
const BROKER_POLL_INTERVAL: Duration = Duration::from_millis(25);
const BROKER_IDLE_TIMEOUT: Duration = Duration::from_secs(120);
const PREPARE_OWNERSHIP_TIMEOUT: Duration = Duration::from_secs(120);
const CLEANUP_DELAY: Duration = Duration::from_secs(10);

static CLAIM_COUNTER: AtomicU64 = AtomicU64::new(1);
static RECEIPT_COUNTER: AtomicU64 = AtomicU64::new(1);

#[derive(Debug, Deserialize)]
#[serde(tag = "operation", deny_unknown_fields)]
enum BrokerRequest {
    #[serde(rename = "prepare-private-directory")]
    Prepare {
        schema: u32,
        request_nonce: String,
        private_directory: PathBuf,
        pending_token: PathBuf,
        receipt: PathBuf,
        expected_receipt: String,
        launch_owned: PathBuf,
    },
    #[serde(rename = "launch-helper")]
    Launch {
        schema: u32,
        request_nonce: String,
        private_directory: PathBuf,
        token_file: Option<PathBuf>,
        handshake_file: PathBuf,
        launch_owned: PathBuf,
        launch_started: PathBuf,
        launch_failed: PathBuf,
        helper_exited: PathBuf,
        launch_cancelled: PathBuf,
        launch_nonce: String,
        expected_build_info: String,
        arguments: Vec<String>,
    },
}

struct ActiveGuard(Arc<AtomicUsize>);

impl Drop for ActiveGuard {
    fn drop(&mut self) {
        self.0.fetch_sub(1, Ordering::AcqRel);
    }
}

pub fn try_run(argv: &[String]) -> Result<bool, String> {
    if argv
        .iter()
        .skip(1)
        .any(|argument| !argument.starts_with("-psn_"))
    {
        return Ok(false);
    }

    let executable = std::env::current_exe()
        .map_err(|error| format!("cannot resolve broker executable: {error}"))?;
    let request_directory = match request_directory(&executable) {
        Some(path) if path.is_dir() => path,
        _ => return Ok(false),
    };
    validate_request_directory(&request_directory)?;

    let active = Arc::new(AtomicUsize::new(0));
    let mut idle_since = Instant::now();
    loop {
        let mut claimed_any = false;
        let entries = match fs::read_dir(&request_directory) {
            Ok(entries) => entries,
            Err(error) => {
                if error.kind() == std::io::ErrorKind::NotFound
                    && active.load(Ordering::Acquire) == 0
                {
                    break;
                }
                eprintln!("pc-capture: macOS broker cannot scan requests: {error}");
                thread::sleep(BROKER_POLL_INTERVAL);
                continue;
            }
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.extension().and_then(|value| value.to_str()) != Some("json") {
                continue;
            }
            let request = match claim_request(&path) {
                Ok(Some(request)) => request,
                Ok(None) => continue,
                Err(error) => {
                    eprintln!("pc-capture: macOS broker rejected request: {error}");
                    continue;
                }
            };
            claimed_any = true;
            active.fetch_add(1, Ordering::AcqRel);
            let worker_active = Arc::clone(&active);
            let worker_executable = executable.clone();
            thread::Builder::new()
                .name("pc-capture-mac-broker".to_string())
                .spawn(move || {
                    let _guard = ActiveGuard(worker_active);
                    if let Err(error) = handle_request(request, &worker_executable) {
                        eprintln!("pc-capture: macOS broker request failed: {error}");
                    }
                })
                .map_err(|error| format!("cannot start macOS broker worker: {error}"))?;
        }

        if claimed_any || active.load(Ordering::Acquire) != 0 {
            idle_since = Instant::now();
        } else if idle_since.elapsed() >= BROKER_IDLE_TIMEOUT {
            break;
        }
        thread::sleep(BROKER_POLL_INTERVAL);
    }
    Ok(true)
}

fn request_directory(executable: &Path) -> Option<PathBuf> {
    let macos = executable.parent()?;
    if macos.file_name()?.to_str()? != "MacOS" {
        return None;
    }
    let contents = macos.parent()?;
    if contents.file_name()?.to_str()? != "Contents" {
        return None;
    }
    let application = contents.parent()?;
    if application.extension()?.to_str()? != "app" {
        return None;
    }
    Some(application.parent()?.join(REQUEST_DIRECTORY_NAME))
}

fn application_directory(executable: &Path) -> Result<PathBuf, String> {
    executable
        .parent()
        .and_then(Path::parent)
        .and_then(Path::parent)
        .filter(|path| path.extension().and_then(|value| value.to_str()) == Some("app"))
        .map(Path::to_path_buf)
        .ok_or_else(|| "broker executable is not inside a macOS application bundle".to_string())
}

fn validate_request_directory(path: &Path) -> Result<(), String> {
    let metadata = fs::symlink_metadata(path)
        .map_err(|error| format!("cannot inspect request directory: {error}"))?;
    if !metadata.file_type().is_dir() || metadata.uid() != effective_uid() {
        return Err("request directory is not an owned directory".to_string());
    }
    if metadata.mode() & 0o022 != 0 {
        return Err("request directory is group- or world-writable".to_string());
    }
    Ok(())
}

fn claim_request(path: &Path) -> Result<Option<BrokerRequest>, String> {
    let file_name = match path.file_name().and_then(|value| value.to_str()) {
        Some(value) => value,
        None => return Ok(None),
    };
    let request_nonce = match file_name.strip_suffix(".json") {
        Some(value) if is_secure_nonce(value) => value,
        _ => return Ok(None),
    };
    let initial = fs::symlink_metadata(path)
        .map_err(|error| format!("cannot inspect request {file_name}: {error}"))?;
    validate_request_metadata(&initial)?;

    let claim = path.with_file_name(format!(
        ".{request_nonce}.processing.{}.{}",
        std::process::id(),
        CLAIM_COUNTER.fetch_add(1, Ordering::Relaxed)
    ));
    match fs::rename(path, &claim) {
        Ok(()) => {}
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(format!("cannot claim request {file_name}: {error}")),
    }

    let result = (|| {
        let metadata = fs::symlink_metadata(&claim)
            .map_err(|error| format!("cannot inspect claimed request: {error}"))?;
        validate_request_metadata(&metadata)?;
        if metadata.len() > MAX_REQUEST_BYTES {
            return Err("request exceeds the size limit".to_string());
        }
        let mut bytes = Vec::with_capacity(metadata.len() as usize);
        OpenOptions::new()
            .read(true)
            .open(&claim)
            .and_then(|mut file| file.read_to_end(&mut bytes))
            .map_err(|error| format!("cannot read claimed request: {error}"))?;
        let request: BrokerRequest = serde_json::from_slice(&bytes)
            .map_err(|error| format!("invalid request JSON: {error}"))?;
        let embedded_nonce = match &request {
            BrokerRequest::Prepare { request_nonce, .. }
            | BrokerRequest::Launch { request_nonce, .. } => request_nonce,
        };
        if embedded_nonce != request_nonce {
            return Err("request nonce does not match its filename".to_string());
        }
        Ok(request)
    })();
    let _ = fs::remove_file(&claim);
    result.map(Some)
}

fn validate_request_metadata(metadata: &fs::Metadata) -> Result<(), String> {
    if !metadata.file_type().is_file() || metadata.uid() != effective_uid() {
        return Err("request is not an owned regular file".to_string());
    }
    if metadata.mode() & 0o022 != 0 {
        return Err("request is group- or world-writable".to_string());
    }
    Ok(())
}

fn handle_request(request: BrokerRequest, executable: &Path) -> Result<(), String> {
    match request {
        BrokerRequest::Prepare {
            schema,
            request_nonce,
            private_directory,
            pending_token,
            receipt,
            expected_receipt,
            launch_owned,
        } => {
            validate_envelope(schema, &request_nonce)?;
            prepare_private_directory(
                private_directory,
                pending_token,
                receipt,
                expected_receipt,
                launch_owned,
            )
        }
        BrokerRequest::Launch {
            schema,
            request_nonce,
            private_directory,
            token_file,
            handshake_file,
            launch_owned,
            launch_started,
            launch_failed,
            helper_exited,
            launch_cancelled,
            launch_nonce,
            expected_build_info,
            arguments,
        } => {
            validate_envelope(schema, &request_nonce)?;
            launch_helper(
                executable,
                private_directory,
                token_file,
                handshake_file,
                launch_owned,
                launch_started,
                launch_failed,
                helper_exited,
                launch_cancelled,
                launch_nonce,
                expected_build_info,
                arguments,
            )
        }
    }
}

fn validate_envelope(schema: u32, nonce: &str) -> Result<(), String> {
    if schema != REQUEST_SCHEMA {
        return Err(format!("unsupported request schema {schema}"));
    }
    if !is_secure_nonce(nonce) {
        return Err("request nonce is not 64 hexadecimal characters".to_string());
    }
    Ok(())
}

fn prepare_private_directory(
    private_directory: PathBuf,
    pending_token: PathBuf,
    receipt: PathBuf,
    expected_receipt: String,
    launch_owned: PathBuf,
) -> Result<(), String> {
    validate_private_path(&private_directory)?;
    validate_direct_child(&private_directory, &pending_token, ".token.pending")?;
    validate_direct_child(&private_directory, &receipt, ".bootstrap-ready")?;
    validate_direct_child(&private_directory, &launch_owned, ".launch-owned")?;
    let receipt_nonce = expected_receipt
        .strip_prefix("perfect-comms-host-action-v1:")
        .ok_or_else(|| "bootstrap receipt has an invalid prefix".to_string())?;
    if !is_secure_nonce(receipt_nonce) {
        return Err("bootstrap receipt nonce is invalid".to_string());
    }

    fs::create_dir(&private_directory)
        .map_err(|error| format!("cannot create private directory: {error}"))?;
    let prepared = (|| {
        fs::set_permissions(&private_directory, fs::Permissions::from_mode(0o700))
            .map_err(|error| format!("cannot secure private directory: {error}"))?;
        validate_existing_private_directory(&private_directory)?;
        let token = OpenOptions::new()
            .write(true)
            .create_new(true)
            .mode(0o600)
            .open(&pending_token)
            .map_err(|error| format!("cannot create pending token: {error}"))?;
        token
            .set_permissions(fs::Permissions::from_mode(0o600))
            .map_err(|error| format!("cannot secure pending token: {error}"))?;
        atomic_write_receipt(&receipt, &expected_receipt)
    })();
    if let Err(error) = prepared {
        let _ = fs::remove_file(&pending_token);
        let _ = fs::remove_file(&receipt);
        let _ = fs::remove_dir(&private_directory);
        return Err(error);
    }

    let started = Instant::now();
    while started.elapsed() < PREPARE_OWNERSHIP_TIMEOUT {
        if !private_directory.is_dir() || launch_owned.exists() {
            return Ok(());
        }
        thread::sleep(Duration::from_secs(1));
    }
    let _ = fs::remove_file(&pending_token);
    let _ = fs::remove_file(&receipt);
    let _ = fs::remove_dir(&private_directory);
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn launch_helper(
    executable: &Path,
    private_directory: PathBuf,
    token_file: Option<PathBuf>,
    handshake_file: PathBuf,
    launch_owned: PathBuf,
    launch_started: PathBuf,
    launch_failed: PathBuf,
    helper_exited: PathBuf,
    launch_cancelled: PathBuf,
    launch_nonce: String,
    expected_build_info: String,
    arguments: Vec<String>,
) -> Result<(), String> {
    validate_existing_private_directory(&private_directory)?;
    validate_direct_child(&private_directory, &handshake_file, "handshake.json")?;
    validate_direct_child(&private_directory, &launch_owned, ".launch-owned")?;
    validate_direct_child(&private_directory, &launch_started, ".launch-started")?;
    validate_direct_child(&private_directory, &launch_failed, ".launch-failed")?;
    validate_direct_child(&private_directory, &helper_exited, ".helper-exited")?;
    validate_direct_child(&private_directory, &launch_cancelled, ".launch-cancelled")?;
    if let Some(path) = &token_file {
        validate_direct_child(&private_directory, path, "token")?;
    }
    if !is_secure_nonce(&launch_nonce) {
        return Err("launch nonce is not 64 hexadecimal characters".to_string());
    }

    atomic_write_receipt(
        &launch_owned,
        &format!("perfect-comms-launch-owned-v1:{launch_nonce}"),
    )?;
    let launch_result = run_owned_launch(
        executable,
        &private_directory,
        token_file.as_deref(),
        &handshake_file,
        &launch_started,
        &helper_exited,
        &launch_cancelled,
        &launch_nonce,
        &expected_build_info,
        &arguments,
    );
    if let Err((reason, code, detail)) = launch_result {
        let _ = atomic_write_receipt(
            &launch_failed,
            &format!("perfect-comms-launch-failed-v1:{launch_nonce}:{reason}:{code}"),
        );
        eprintln!("pc-capture: macOS broker launch failed ({reason}): {detail}");
    }

    thread::sleep(CLEANUP_DELAY);
    cleanup_private_directory(
        &private_directory,
        token_file.as_deref(),
        &handshake_file,
        &launch_owned,
        &launch_started,
        &launch_failed,
        &helper_exited,
        &launch_cancelled,
    );
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn run_owned_launch(
    executable: &Path,
    private_directory: &Path,
    token_file: Option<&Path>,
    handshake_file: &Path,
    launch_started: &Path,
    helper_exited: &Path,
    launch_cancelled: &Path,
    launch_nonce: &str,
    expected_build_info: &str,
    arguments: &[String],
) -> Result<(), (&'static str, i32, String)> {
    if cancellation_requested(launch_cancelled, launch_nonce) {
        return Err((
            "cancelled",
            125,
            "cancelled before helper validation".to_string(),
        ));
    }
    if let Some(path) = token_file {
        fs::set_permissions(path, fs::Permissions::from_mode(0o600)).map_err(|error| {
            (
                "token-permission",
                126,
                format!("cannot secure token file: {error}"),
            )
        })?;
    }

    let mut argv = Vec::with_capacity(arguments.len() + 1);
    argv.push(executable.to_string_lossy().into_owned());
    argv.extend(arguments.iter().cloned());
    let parsed = super::parse_args(&argv).map_err(|error| ("invalid-arguments", 64, error))?;
    validate_runtime_arguments(
        &parsed,
        token_file,
        handshake_file,
        launch_cancelled,
        launch_nonce,
    )
    .map_err(|error| ("invalid-arguments", 64, error))?;

    let actual_build_info =
        build_info::build_info_json().map_err(|error| ("build-info", 65, error))?;
    if actual_build_info != expected_build_info {
        return Err((
            "build-contract",
            65,
            "helper build information does not match the managed contract".to_string(),
        ));
    }
    if cancellation_requested(launch_cancelled, launch_nonce) {
        return Err((
            "cancelled",
            125,
            "cancelled before helper start".to_string(),
        ));
    }

    if let Ok(application) = application_directory(executable) {
        strip_quarantine_bounded(&application);
    }
    let stderr_path = private_directory.join(".helper-stderr");
    let stderr = OpenOptions::new()
        .write(true)
        .create(true)
        .truncate(true)
        .mode(0o600)
        .open(&stderr_path)
        .map_err(|error| {
            (
                "stderr-file",
                126,
                format!("cannot open helper stderr: {error}"),
            )
        })?;
    let mut child = Command::new(executable)
        .args(arguments)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::from(stderr))
        .spawn()
        .map_err(|error| ("spawn-failed", 126, format!("cannot start helper: {error}")))?;
    let helper_pid = child.id();
    if let Err(error) = atomic_write_receipt(
        launch_started,
        &format!("perfect-comms-launch-started-v1:{launch_nonce}:{helper_pid}"),
    ) {
        let _ = child.kill();
        let _ = child.wait();
        return Err(("start-receipt", 125, error));
    }

    let status = child.wait().map_err(|error| {
        (
            "wait-failed",
            125,
            format!("cannot wait for helper: {error}"),
        )
    })?;
    let exit_code = status
        .code()
        .or_else(|| status.signal().map(|signal| 128 + signal))
        .unwrap_or(125);
    let _ = atomic_write_receipt(
        helper_exited,
        &format!("perfect-comms-helper-exited-v1:{launch_nonce}:{helper_pid}:{exit_code}"),
    );
    Ok(())
}

fn validate_runtime_arguments(
    parsed: &super::Args,
    token_file: Option<&Path>,
    handshake_file: &Path,
    launch_cancelled: &Path,
    launch_nonce: &str,
) -> Result<(), String> {
    if parsed.handshake_path.as_deref() != Some(handshake_file) {
        return Err("handshake argument does not match the request".to_string());
    }
    if parsed.token_file.as_deref() != token_file {
        return Err("token argument does not match the request".to_string());
    }
    if parsed.cancel_file.as_deref() != Some(launch_cancelled)
        || parsed.cancel_nonce.as_deref() != Some(launch_nonce)
    {
        return Err("cancellation arguments do not match the request".to_string());
    }
    if parsed.enumerate != token_file.is_none()
        || parsed.protocol_version
        || parsed.build_info
        || parsed.owner_pid.is_some()
        || parsed.synthetic
    {
        return Err("request contains an unsupported helper mode".to_string());
    }
    Ok(())
}

fn validate_private_path(path: &Path) -> Result<(), String> {
    if !path.is_absolute()
        || path
            .components()
            .any(|component| !matches!(component, Component::RootDir | Component::Normal(_)))
    {
        return Err("private directory path is not absolute and normalized".to_string());
    }
    let parent = path
        .parent()
        .ok_or_else(|| "private directory has no parent".to_string())?;
    if parent != Path::new("/tmp") && parent != Path::new("/private/tmp") {
        return Err("private directory is outside the host temporary root".to_string());
    }
    let leaf = path
        .file_name()
        .and_then(|value| value.to_str())
        .ok_or_else(|| "private directory name is not UTF-8".to_string())?;
    let nonce = leaf
        .strip_prefix("perfect-comms-")
        .ok_or_else(|| "private directory name has an invalid prefix".to_string())?;
    if nonce.len() != 32 || !nonce.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err("private directory name has an invalid nonce".to_string());
    }
    Ok(())
}

fn validate_existing_private_directory(path: &Path) -> Result<(), String> {
    validate_private_path(path)?;
    let metadata = fs::symlink_metadata(path)
        .map_err(|error| format!("cannot inspect private directory: {error}"))?;
    if !metadata.file_type().is_dir()
        || metadata.uid() != effective_uid()
        || metadata.mode() & 0o777 != 0o700
    {
        return Err("private directory ownership or mode is invalid".to_string());
    }
    Ok(())
}

fn validate_direct_child(parent: &Path, path: &Path, expected_name: &str) -> Result<(), String> {
    if path.parent() != Some(parent)
        || path.file_name().and_then(|value| value.to_str()) != Some(expected_name)
    {
        return Err(format!("invalid private control path for {expected_name}"));
    }
    Ok(())
}

fn atomic_write_receipt(path: &Path, value: &str) -> Result<(), String> {
    let parent = path
        .parent()
        .ok_or_else(|| "receipt has no containing directory".to_string())?;
    validate_existing_private_directory(parent)?;
    let name = path
        .file_name()
        .and_then(|value| value.to_str())
        .ok_or_else(|| "receipt filename is not UTF-8".to_string())?;
    let temporary = parent.join(format!(
        ".{name}.tmp.{}.{}",
        std::process::id(),
        RECEIPT_COUNTER.fetch_add(1, Ordering::Relaxed)
    ));
    let result = (|| {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .mode(0o600)
            .open(&temporary)
            .map_err(|error| format!("cannot create receipt: {error}"))?;
        file.write_all(value.as_bytes())
            .map_err(|error| format!("cannot write receipt: {error}"))?;
        file.set_permissions(fs::Permissions::from_mode(0o600))
            .map_err(|error| format!("cannot secure receipt: {error}"))?;
        fs::rename(&temporary, path).map_err(|error| format!("cannot publish receipt: {error}"))
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result
}

fn cancellation_requested(path: &Path, nonce: &str) -> bool {
    fs::read_to_string(path)
        .map(|value| value == format!("perfect-comms-launch-cancel-v1:{nonce}"))
        .unwrap_or(false)
}

fn strip_quarantine_bounded(application: &Path) {
    let mut child = match Command::new("/usr/bin/xattr")
        .args(["-dr", "com.apple.quarantine"])
        .arg(application)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
    {
        Ok(child) => child,
        Err(_) => return,
    };
    let started = Instant::now();
    while started.elapsed() < Duration::from_secs(2) {
        match child.try_wait() {
            Ok(Some(_)) => return,
            Ok(None) => thread::sleep(BROKER_POLL_INTERVAL),
            Err(_) => return,
        }
    }
    let _ = child.kill();
    let _ = child.wait();
}

#[allow(clippy::too_many_arguments)]
fn cleanup_private_directory(
    private_directory: &Path,
    token_file: Option<&Path>,
    handshake_file: &Path,
    launch_owned: &Path,
    launch_started: &Path,
    launch_failed: &Path,
    helper_exited: &Path,
    launch_cancelled: &Path,
) {
    if let Some(path) = token_file {
        let _ = fs::remove_file(path);
    }
    for path in [
        private_directory.join(".token.pending"),
        private_directory.join(".bootstrap-ready"),
        private_directory.join(".helper-stderr"),
        handshake_file.to_path_buf(),
        launch_owned.to_path_buf(),
        launch_started.to_path_buf(),
        launch_failed.to_path_buf(),
        helper_exited.to_path_buf(),
        launch_cancelled.to_path_buf(),
    ] {
        let _ = fs::remove_file(path);
    }
    let _ = fs::remove_dir(private_directory);
}

fn is_secure_nonce(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn effective_uid() -> u32 {
    unsafe { libc::geteuid() }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn derives_request_directory_from_application_executable() {
        let executable =
            Path::new("/cache/bundle/PerfectCommsAudio.app/Contents/MacOS/PerfectCommsAudio");
        assert_eq!(
            request_directory(executable).unwrap(),
            Path::new("/cache/bundle").join(REQUEST_DIRECTORY_NAME)
        );
    }

    #[test]
    fn rejects_private_directories_outside_tmp() {
        assert!(validate_private_path(Path::new(
            "/tmp/perfect-comms-0123456789abcdef0123456789abcdef"
        ))
        .is_ok());
        assert!(validate_private_path(Path::new(
            "/Users/test/perfect-comms-0123456789abcdef0123456789abcdef"
        ))
        .is_err());
    }
}
