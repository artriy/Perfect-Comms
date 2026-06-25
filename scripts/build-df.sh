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
#   mac        -> libdf.dylib   (universal: x86_64 + arm64 via lipo)

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
src="$root/native/third_party/deep-filter"
target="${1:-}"

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

distdir="$root/artifacts/df"
mkdir -p "$distdir"

if [[ "$target" == "mac" ]]; then
  # Universal cdylib so the dylib loads in either a native arm64 host process or
  # an x86_64 (Rosetta / Intel) host process. The x86_64 slice is built at the
  # baseline cpu (no AVX) so it survives Rosetta 2 and older Intel Macs.
  build="$root/build-df-mac"
  rustup target add x86_64-apple-darwin aarch64-apple-darwin >/dev/null 2>&1 || true
  echo "==> cargo build deep_filter --features capi (mac x86_64-apple-darwin)"
  RUSTFLAGS="-C target-cpu=x86-64" cargo build --release --no-default-features --features capi -p deep_filter \
    --manifest-path "$src/Cargo.toml" --target x86_64-apple-darwin --target-dir "$build"
  echo "==> cargo build deep_filter --features capi (mac aarch64-apple-darwin)"
  cargo build --release --no-default-features --features capi -p deep_filter \
    --manifest-path "$src/Cargo.toml" --target aarch64-apple-darwin --target-dir "$build"
  x64="$build/x86_64-apple-darwin/release/libdf.dylib"
  arm="$build/aarch64-apple-darwin/release/libdf.dylib"
  dest="$distdir/libdf.dylib"
  lipo -create -output "$dest" "$x64" "$arm"
  lipo -info "$dest"
  echo "DF_LIB=$dest"
  exit 0
fi

case "$target" in
  win-x64)   triple="x86_64-pc-windows-gnu";    lib="df.dll";    out="df.x64.dll" ;;
  win-x86)   triple="i686-pc-windows-gnu";      lib="df.dll";    out="df.x86.dll" ;;
  linux-x64) triple="x86_64-unknown-linux-gnu"; lib="libdf.so";  out="libdf.so" ;;
  *) echo "usage: build-df.sh {win-x64|win-x86|linux-x64|mac}" >&2; exit 2 ;;
esac

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
