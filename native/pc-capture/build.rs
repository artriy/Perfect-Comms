fn main() {
    #[cfg(windows)]
    {
        // Build scripts are compiled for the host. A Windows maintainer can still be
        // cross-compiling pc-capture for Android, macOS, or Linux, where an .rc resource
        // is invalid. Only invoke winresource when Cargo's actual target is Windows.
        if std::env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
            return;
        }

        let mut res = winresource::WindowsResource::new();
        res.set_icon("icon.ico");
        res.set("FileDescription", "PerfectComms Audio Helper");
        res.set("ProductName", "PerfectComms");
        res.set("CompanyName", "PerfectComms");
        res.set("OriginalFilename", "PerfectCommsAudio.exe");
        res.set("InternalName", "PerfectCommsAudio");
        res.set("LegalCopyright", "PerfectComms");
        res.compile()
            .expect("failed to embed PerfectComms Windows executable resources");
    }
}
