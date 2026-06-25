fn main() {
    #[cfg(windows)]
    {
        let mut res = winresource::WindowsResource::new();
        res.set_icon("icon.ico");
        res.set("FileDescription", "PerfectComms Audio Helper");
        res.set("ProductName", "PerfectComms");
        res.set("CompanyName", "PerfectComms");
        res.set("OriginalFilename", "PerfectCommsAudio.exe");
        res.set("InternalName", "PerfectCommsAudio");
        res.set("LegalCopyright", "PerfectComms");
        if let Err(e) = res.compile() {
            eprintln!("cargo:warning=resource embed failed: {e}");
        }
    }
}
