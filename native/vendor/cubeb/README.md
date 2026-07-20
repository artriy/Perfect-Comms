# Vendored cubeb-rs 0.36.0 wrapper

This is the ISC-licensed `cubeb` 0.36.0 high-level Rust wrapper. PerfectComms carries one
local lifecycle fix in `StreamBuilder::init`: callback ownership remains guarded until native
stream initialization and optional callback registration both succeed. This prevents failed
device-open attempts from leaking the callback allocation and everything captured by it.

The native libcubeb implementation remains supplied by the unmodified `cubeb-core` /
`cubeb-sys` 0.36.0 crates.
