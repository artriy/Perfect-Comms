# Cargo's `+crt-static` target feature makes Rust and `cc`-built objects use
# the static MSVC runtime. CMake dependencies need the equivalent policy and
# runtime selection before their first `project()` call.
if(POLICY CMP0091)
  cmake_policy(SET CMP0091 NEW)
endif()

set(
  CMAKE_MSVC_RUNTIME_LIBRARY
  "MultiThreaded"
  CACHE STRING "Use the static MSVC runtime for native helper dependencies" FORCE
)

# opusic-sys bundles a newer Opus CMake project which explicitly chooses /MD
# unless this project option is enabled. Keep it aligned with Cargo's static
# runtime selection.
set(OPUS_STATIC_RUNTIME ON CACHE BOOL "Build bundled Opus with the static MSVC runtime" FORCE)

# cubeb-sys already forces Cubeb's static MSVC runtime. This additional option
# prevents CMake from discovering and linking a machine-local SpeexDSP build.
set(BUNDLE_SPEEX ON CACHE BOOL "Build Cubeb's vendored Speex resampler" FORCE)
