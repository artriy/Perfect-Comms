# Perfect Comms cubeb-sys patch

Source: crates.io `cubeb-sys` 0.36.0 archive, SHA-256
`1227463346ba02e5b6adff179c9871273dbec40808d6ba576d57ed995b02675a`.

The vendored source differs from that archive only in these release-integrity changes:

- `src/context.rs` declares libcubeb's public `cubeb_get_backend_names` ABI so release helpers can
  report the backends actually compiled into the linked C library.
- `libcubeb/CMakeLists.txt` passes `--locked` to its nested PulseAudio and CoreAudio Rust builds.
- The nested PulseAudio and CoreAudio Rust backends use `ringbuf` 0.5.2 APIs and lockfiles to
  address [RUSTSEC-2026-0293](https://rustsec.org/advisories/RUSTSEC-2026-0293.html). Their sample
  buffers contain `i16`/`f32`; CoreAudio callback logging contains primitive fields without `Drop`.
  These consumers cannot trigger the advisory's panicking-destructor path, but the vulnerable
  dependency is removed. Windows WASAPI does not use either nested Rust backend. Perfect Comms'
  Linux dependency enables `unittest-build`, selecting the C backends rather than Rust PulseAudio.
  Other Rust PulseAudio builds require detected PulseAudio headers; macOS selects Rust CoreAudio
  when Apple AudioUnit is available.

The root and nested `Cargo.lock` files are intentionally tracked. Do not remove them: clean-checkout
macOS builds rely on the CoreAudio lock, and the PulseAudio lock protects any build that enables the
Rust Pulse backend.
