#!/usr/bin/env bash
set -euo pipefail

# Build the vendored DeepFilterNet libDF crate as a SHARED library (cdylib) for
# one target. Dynamic loading (df-sys/libloading) means the lib only has to
# export the df_* C ABI; the helper compiler need not match this toolchain.
#
# Targets:
#   win-x64    -> df.x64.dll    (mingw cross: x86_64-pc-windows-gnu, crt-static)
#   win-x86    -> df.x86.dll    (mingw cross: i686-pc-windows-gnu,   crt-static)
#   linux-x64  -> libdf.so      (native)
#   mac        -> libdf.dylib   (native, host arch)

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
src="$root/native/third_party/deep-filter"
target="${1:-}"

case "$target" in
  win-x64)   triple="x86_64-pc-windows-gnu";    lib="df.dll";    out="df.x64.dll" ;;
  win-x86)   triple="i686-pc-windows-gnu";      lib="df.dll";    out="df.x86.dll" ;;
  linux-x64) triple="x86_64-unknown-linux-gnu"; lib="libdf.so";  out="libdf.so" ;;
  mac)       triple="$(rustc -vV | sed -n 's/^host: //p')"; lib="libdf.dylib"; out="libdf.dylib" ;;
  *) echo "usage: build-df.sh {win-x64|win-x86|linux-x64|mac}" >&2; exit 2 ;;
esac

if [[ ! -f "$src/Cargo.toml" ]]; then
  echo "ERROR: submodule missing at $src (run: git submodule update --init --recursive)" >&2
  exit 1
fi

# Patch libDF's C API so df_create("") falls back to the embedded default model
# (DfParams::default()) instead of trying to load a file path. Documented in
# Libs/df-build.md. Idempotent: skip if already applied.
capi="$src/libDF/src/capi.rs"
if ! grep -q 'DfParams::default()' "$capi"; then
  perl -0777 -pi -e 's/        let df_params =\n            DfParams::new\(PathBuf::from\(model_path\)\)\.expect\("Could not load model from path"\);/        let df_params = if model_path.is_empty() {\n            DfParams::default()\n        } else {\n            DfParams::new(PathBuf::from(model_path)).expect("Could not load model from path")\n        };/' "$capi"
fi
grep -q 'DfParams::default()' "$capi" || { echo "ERROR: capi empty-model patch did not apply (upstream changed?)" >&2; exit 1; }

case "$target" in
  win-x64)
    export CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER="x86_64-w64-mingw32-gcc"
    export RUSTFLAGS="-C target-feature=+crt-static"
    ;;
  win-x86)
    export CARGO_TARGET_I686_PC_WINDOWS_GNU_LINKER="i686-w64-mingw32-gcc"
    export RUSTFLAGS="-C target-feature=+crt-static"
    ;;
esac

rustup target add "$triple" >/dev/null 2>&1 || true

build="$root/build-df-$target"
distdir="$root/artifacts/df"
mkdir -p "$distdir"

echo "==> cargo build deep_filter --features capi ($target -> $triple)"
cargo build --release --no-default-features --features capi -p deep_filter \
  --manifest-path "$src/Cargo.toml" \
  --target "$triple" \
  --target-dir "$build"

built="$build/$triple/release/$lib"
if [[ ! -f "$built" ]]; then
  echo "ERROR: expected cdylib not found: $built" >&2
  find "$build/$triple/release" -maxdepth 1 -name "*df*" >&2 || true
  exit 1
fi

dest="$distdir/$out"
cp "$built" "$dest"
echo "built: $built"
echo "DF_LIB=$dest"
