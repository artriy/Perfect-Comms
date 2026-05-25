cmake_minimum_required(VERSION 3.20)

if(CMAKE_HOST_SYSTEM_NAME STREQUAL "Darwin" AND NOT DEFINED CMAKE_OSX_DEPLOYMENT_TARGET)
    set(CMAKE_OSX_DEPLOYMENT_TARGET "11.0" CACHE STRING "Minimum macOS deployment target" FORCE)
endif()

project(PerfectCommsNativeAudioMacOS C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

set(MINIAUDIO_VERSION "0.11.22" CACHE STRING "miniaudio version")
set(MINIAUDIO_HEADER "${CMAKE_CURRENT_BINARY_DIR}/miniaudio.h")

if(NOT EXISTS "${MINIAUDIO_HEADER}")
    file(DOWNLOAD
        "https://raw.githubusercontent.com/mackron/miniaudio/${MINIAUDIO_VERSION}/miniaudio.h"
        "${MINIAUDIO_HEADER}"
        TLS_VERIFY ON
        STATUS MINIAUDIO_DOWNLOAD_STATUS)
    list(GET MINIAUDIO_DOWNLOAD_STATUS 0 MINIAUDIO_DOWNLOAD_CODE)
    if(NOT MINIAUDIO_DOWNLOAD_CODE EQUAL 0)
        message(FATAL_ERROR "Could not download miniaudio.h. Download it manually into ${MINIAUDIO_HEADER}.")
    endif()
endif()

add_library(perfectcomms_audio_macos SHARED
    src/perfectcomms_audio.c)

target_include_directories(perfectcomms_audio_macos PRIVATE
    "${CMAKE_CURRENT_BINARY_DIR}"
    "${CMAKE_CURRENT_SOURCE_DIR}/include")

if(APPLE)
    target_link_libraries(perfectcomms_audio_macos PRIVATE
        "-framework CoreAudio"
        "-framework AudioToolbox"
        "-framework AudioUnit"
        "-framework CoreFoundation")
    set_target_properties(perfectcomms_audio_macos PROPERTIES
        OUTPUT_NAME "perfectcomms_audio_macos")
endif()
