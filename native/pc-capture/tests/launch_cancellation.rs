use std::fs;
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

struct ChildGuard(Child);

impl Drop for ChildGuard {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

struct DirectoryGuard(PathBuf);

impl Drop for DirectoryGuard {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

#[test]
fn live_helper_honors_nonce_bound_launch_cancellation() {
    let unique = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    let directory = std::env::temp_dir().join(format!(
        "perfect-comms-live-cancel-{}-{unique}",
        std::process::id()
    ));
    fs::create_dir(&directory).unwrap();
    let _directory_guard = DirectoryGuard(directory.clone());

    let handshake = directory.join("handshake.json");
    let token = directory.join("token");
    let cancellation = directory.join("launch-cancelled");
    let nonce = "A".repeat(64);
    fs::write(&token, "integration-secret").unwrap();

    let child = Command::new(env!("CARGO_BIN_EXE_pc-capture"))
        .arg("--handshake")
        .arg(&handshake)
        .arg("--token-file")
        .arg(&token)
        .arg("--cancel-file")
        .arg(&cancellation)
        .arg("--cancel-nonce")
        .arg(&nonce)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    let mut child = ChildGuard(child);

    let ready_deadline = Instant::now() + Duration::from_secs(5);
    while !handshake.is_file() && Instant::now() < ready_deadline {
        assert!(
            child.0.try_wait().unwrap().is_none(),
            "helper exited before publishing its handshake"
        );
        thread::sleep(Duration::from_millis(25));
    }
    assert!(handshake.is_file(), "helper did not publish a handshake");
    assert!(
        !token.exists(),
        "helper did not unlink the consumed authentication token"
    );

    fs::write(
        &cancellation,
        format!("perfect-comms-launch-cancel-v1:{}", "B".repeat(64)),
    )
    .unwrap();
    thread::sleep(Duration::from_millis(250));
    assert!(
        child.0.try_wait().unwrap().is_none(),
        "helper accepted a cancellation receipt with the wrong nonce"
    );

    fs::write(
        &cancellation,
        format!("perfect-comms-launch-cancel-v1:{nonce}"),
    )
    .unwrap();
    let exit_deadline = Instant::now() + Duration::from_secs(5);
    let status = loop {
        if let Some(status) = child.0.try_wait().unwrap() {
            break status;
        }
        assert!(
            Instant::now() < exit_deadline,
            "helper ignored a valid launch cancellation receipt"
        );
        thread::sleep(Duration::from_millis(25));
    };
    assert!(status.success(), "cancelled helper exited with {status}");
}
