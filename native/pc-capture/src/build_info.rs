use crate::proto::PROTO_VERSION;
use cubeb::ffi;
use serde::Serialize;
use std::ffi::CStr;

pub const CUBEB_VERSION: &str = "0.36.0";
pub const AUDIO_CONTRACT: &str = "PERFECTCOMMS_AUDIO_CONTRACT=1;ENGINE=CUBEB;CUBEB=0.36.0;";
const MAX_COMPILED_BACKENDS: usize = 32;

pub fn compiled_backend_names() -> Result<Vec<String>, String> {
    // SAFETY: the returned array and strings have static libcubeb lifetime and are read-only.
    let backends = unsafe { ffi::cubeb_get_backend_names() };
    if backends.count > MAX_COMPILED_BACKENDS {
        return Err(format!(
            "libcubeb reported an invalid backend count {}",
            backends.count
        ));
    }
    if backends.count != 0 && backends.names.is_null() {
        return Err("libcubeb returned a null backend array".to_string());
    }

    let mut names = Vec::with_capacity(backends.count);
    for index in 0..backends.count {
        // SAFETY: count was bounded and libcubeb promises an array of `count` pointers.
        let name = unsafe { *backends.names.add(index) };
        if name.is_null() {
            return Err(format!("libcubeb backend {index} is null"));
        }
        // SAFETY: every backend name is a static NUL-terminated C string.
        let name = unsafe { CStr::from_ptr(name) }
            .to_str()
            .map_err(|_| format!("libcubeb backend {index} is not UTF-8"))?;
        if name.is_empty() || name.len() > 64 {
            return Err(format!("libcubeb backend {index} has an invalid name"));
        }
        names.push(name.to_string());
    }
    names.sort_unstable();
    if names.windows(2).any(|pair| pair[0] == pair[1]) {
        return Err("libcubeb reported duplicate compiled backends".to_string());
    }
    Ok(names)
}

#[derive(Serialize)]
struct BuildInfo<'a> {
    schema: u32,
    protocol: u32,
    audio_engine: &'a str,
    cubeb_version: &'a str,
    compiled_backends: Vec<String>,
    contract: &'a str,
}

pub fn build_info_json() -> Result<String, String> {
    serde_json::to_string(&BuildInfo {
        schema: 1,
        protocol: PROTO_VERSION,
        audio_engine: "cubeb",
        cubeb_version: CUBEB_VERSION,
        compiled_backends: compiled_backend_names()?,
        contract: AUDIO_CONTRACT,
    })
    .map_err(|error| format!("serialize build info: {error}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn build_info_proves_the_linked_cubeb_backend_inventory() {
        let value: serde_json::Value = serde_json::from_str(&build_info_json().unwrap()).unwrap();
        assert_eq!(value["schema"], 1);
        assert_eq!(value["protocol"], PROTO_VERSION);
        assert_eq!(value["audio_engine"], "cubeb");
        assert_eq!(value["cubeb_version"], CUBEB_VERSION);
        assert_eq!(value["contract"], AUDIO_CONTRACT);
        let names = value["compiled_backends"].as_array().unwrap();
        assert!(!names.is_empty());
        #[cfg(windows)]
        {
            assert!(names.iter().any(|name| name == "wasapi"));
            assert!(names.iter().any(|name| name == "winmm"));
        }
        #[cfg(target_os = "linux")]
        {
            assert!(names.iter().any(|name| name == "pulse"));
            assert!(names.iter().any(|name| name == "alsa"));
        }
        #[cfg(target_os = "macos")]
        assert!(names.iter().any(|name| name == "audiounit"));
    }
}
