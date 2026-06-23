mod audio;
mod ipc;
mod proto;

fn main() {
    eprintln!("pc-capture {}", env!("CARGO_PKG_VERSION"));
    std::process::exit(0);
}
