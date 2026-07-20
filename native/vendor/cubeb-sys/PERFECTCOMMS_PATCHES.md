# Perfect Comms cubeb-sys patch

Source: crates.io `cubeb-sys` 0.36.0 archive, SHA-256
`1227463346ba02e5b6adff179c9871273dbec40808d6ba576d57ed995b02675a`.

The vendored source differs from that archive only in these release-integrity changes:

- `src/context.rs` declares libcubeb's public `cubeb_get_backend_names` ABI so release helpers can
  report the backends actually compiled into the linked C library.
- `libcubeb/CMakeLists.txt` passes `--locked` to its nested PulseAudio and CoreAudio Rust builds.

The root and nested `Cargo.lock` files are intentionally tracked. Do not remove them: clean-checkout
macOS builds rely on the CoreAudio lock, and the PulseAudio lock protects any build that enables the
Rust Pulse backend.
