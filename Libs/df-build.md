# Rebuilding df.x64.dll (DeepFilterNet 3 noise suppressor, libDF cdylib)

This embedded native library is the Windows x64 neural noise suppressor used on the Windows build
(`DeepFilterDenoiser.cs`). It is the `libDF` crate compiled as a cdylib with its C API and the default
DeepFilterNet 3 model embedded, stripped so the shipped DLL depends only on Windows system libraries
(`KERNEL32`, `ntdll`, `bcrypt`, UCRT) — no Rust/mingw runtime DLL required.

## Source

- Upstream: https://github.com/Rikorose/DeepFilterNet
- Pinned commit: `d375b2d8309e0935d165700c91da9de862a99c31` (2024-10-17)
- License: dual MIT OR Apache-2.0. See `df.COPYING`.
- Embedded model: the default DeepFilterNet 3 model is compiled into the cdylib via the `default-model`
  feature (pulled in by `capi`). Model authorship: Hendrik Schroeter / Rikorose DeepFilterNet.

## Local patch (derivative-work disclosure)

`libDF/src/capi.rs` is patched so `df_create` accepts an EMPTY model path and falls back to the embedded
default model (`DfParams::default()`) instead of trying to load a file path. The plugin calls `df_create("")`
to use the embedded model. Patch:

```rust
// in DFState::new, replace:
//   let df_params = DfParams::new(PathBuf::from(model_path)).expect("Could not load model from path");
// with:
let df_params = if model_path.is_empty() {
    DfParams::default()
} else {
    DfParams::new(PathBuf::from(model_path)).expect("Could not load model from path")
};
```

## Compile (cargo + MinGW-w64, from the DeepFilterNet checkout root)

The host toolchain is MSVC but there is no MSVC linker available, so build the GNU target with mingw:

```
rustup target add x86_64-pc-windows-gnu
cargo build --release --no-default-features --features capi -p deep_filter --target x86_64-pc-windows-gnu
strip -s target/x86_64-pc-windows-gnu/release/df.dll
```

Copy `df.dll` to `PerfectComms/Libs/df.x64.dll`. It is embedded via `<EmbeddedResource>` in
`PerfectComms.csproj` with logical name `Lib.df.x64.dll`, matching `DeepFilterDenoiser.ResourceName`.

Verify the C API is exported: `objdump -p df.x64.dll | grep df_` should list `df_create`,
`df_get_frame_length`, `df_process_frame`, `df_free`.
