#include "bridge.hpp"
#include <obs-module.h>
#include <obs-frontend-api.h>
#include <atomic>
#include <memory>
#include <exception>
#include <mutex>
#include <util/platform.h>

OBS_DECLARE_MODULE()
OBS_MODULE_AUTHOR("Perfect Comms")

namespace {
std::unique_ptr<pcobs::Bridge> bridge;
obs_output_t *recording_output = nullptr;
std::atomic<bool> armed{false};
std::atomic<bool> capture_started{false};
std::atomic<bool> capture_finished{false};
std::atomic<bool> clock_failed{false};
std::atomic<bool> stop_signalled{false};
std::mutex packet_mutex;
pcobs::PacketAcceptance packets;
bool mux_headers_pending = false;
bool normalize_presentation = false;
std::optional<int64_t> presentation_origin;

const char *source_name(void *) { return "Perfect Comms"; }
void sync_offset(void *, calldata_t *data)
{
    if (calldata_int(data, "offset") != 0) {
        calldata_set_int(data, "offset", 0);
        if (bridge) bridge->fail("Perfect Comms Sync Offset must remain 0 ms. Offset changes are rejected to preserve stem alignment.");
    }
}
void *source_create(obs_data_t *, obs_source_t *source)
{
    if (!bridge) return nullptr;
    try {
        bridge->add_source(source);
        signal_handler_connect(obs_source_get_signal_handler(source), "audio_sync", sync_offset, source);
        return source;
    } catch (const std::exception &error) { bridge->fail(error.what()); return nullptr; }
}
void source_destroy(void *data)
{
    auto *source = static_cast<obs_source_t *>(data);
    signal_handler_disconnect(obs_source_get_signal_handler(source), "audio_sync", sync_offset, source);
    if (bridge) bridge->remove_source(source);
}
bool refresh_status(obs_properties_t *properties, obs_property_t *, void *)
{
    if (bridge) {
        const auto status = bridge->status();
        const auto location = bridge->location();
        obs_property_set_description(obs_properties_get(properties, "status"), status.c_str());
        obs_property_set_description(obs_properties_get(properties, "location"), location.empty() ? "No recording folder yet." : location.c_str());
    }
    return true;
}
obs_properties_t *source_properties(void *)
{
    auto *properties = obs_properties_create();
    obs_properties_add_text(properties, "status", "", OBS_TEXT_INFO);
    obs_properties_add_text(properties, "location", "", OBS_TEXT_INFO);
    obs_properties_add_text(properties, "instructions",
        "Add this source once, then reuse it in scenes. Duplicate instances stay silent. OBS Start/Stop/Pause controls stems automatically. "
        "Keep Sync Offset at 0 ms. Configure your mic and game audio separately in OBS; disable duplicate application/desktop voice capture. "
        "Turn off automatic recording file splitting. Stem mode changes apply on the next recording. Stems contain the mod's audible incoming voices, before OBS source filters and fader.", OBS_TEXT_INFO);
    obs_properties_add_button2(properties, "refresh", "Refresh connection and recording status", refresh_status, nullptr);
    refresh_status(properties, nullptr, nullptr);
    return properties;
}
void timing_failure(const char *message)
{
    if (!clock_failed.exchange(true) && bridge) bridge->stop_stems(message);
}
void accepted_clock(std::optional<pcobs::Clock> clock)
{
    if (!clock) return;
    pcobs::Event event;
    event.kind = pcobs::EventKind::clock;
    event.clock = *clock;
    bridge->post(event);
}
void packet_clock(obs_output_t *output, encoder_packet *packet, encoder_packet_time *timing, void *)
{
    if (!bridge || !armed.load() || clock_failed.load()) return;
    try {
        std::lock_guard lock(packet_mutex);
        const auto bytes = obs_output_get_total_bytes(output);
        accepted_clock(packets.resolve(bytes, stop_signalled.load()));
        if (stop_signalled.load()) return;
        std::optional<pcobs::Clock> clock;
        std::optional<pcobs::VideoDecodeTime> decode;
        if (packet->type == OBS_ENCODER_VIDEO && packet->track_idx == 0) {
            if (normalize_presentation) decode = pcobs::VideoDecodeTime{packet->dts, packet->timebase_den};
            if (normalize_presentation && !presentation_origin) presentation_origin = packet->pts;
            clock = pcobs::video_clock(packet->pts, packet->timebase_num, packet->timebase_den, timing ? timing->cts : 0, presentation_origin.value_or(0));
        }
        uint64_t header_bytes = 0;
        if (mux_headers_pending) {
            const auto add_header = [&](obs_encoder_t *encoder) {
                uint8_t *bytes = nullptr;
                size_t size = 0;
                if (!obs_encoder_get_extra_data(encoder, &bytes, &size) || size > UINT64_MAX - header_bytes)
                    throw std::runtime_error("OBS recording encoder headers are unavailable. Voice stems stopped.");
                header_bytes += size;
            };
            add_header(obs_output_get_video_encoder(output));
            for (size_t index = 0; index < MAX_AUDIO_MIXES; ++index) {
                auto *encoder = obs_output_get_audio_encoder(output, index);
                if (!encoder) break;
                add_header(encoder);
            }
            mux_headers_pending = false;
        }
        packets.submit(bytes, packet->size, clock, header_bytes, decode);
    } catch (const std::exception &error) { timing_failure(error.what()); }
}
void output_stopped(void *, calldata_t *data)
{
    stop_signalled.store(true);
    if (calldata_int(data, "code") != OBS_OUTPUT_SUCCESS)
        timing_failure("OBS recording output failed. Voice stems may be incomplete; check the OBS recording error.");
}
void deactivated(void *, calldata_t *data)
{
    if (!bridge || capture_finished.exchange(true)) return;
    armed.store(false);
    try {
        std::lock_guard lock(packet_mutex);
        if (!clock_failed.load()) accepted_clock(packets.resolve(obs_output_get_total_bytes(
            static_cast<obs_output_t *>(calldata_ptr(data, "output"))), true));
        packets.reset();
    } catch (const std::exception &error) { timing_failure(error.what()); }
    pcobs::Event event;
    event.kind = pcobs::EventKind::stop;
    bridge->post(event);
}
void activated(void *, calldata_t *)
{
    if (!bridge || !recording_output || !armed.load()) return;
    capture_started.store(true);
    auto *settings = obs_output_get_settings(recording_output);
    if (obs_data_get_bool(settings, "split_file")) {
        obs_data_release(settings);
        timing_failure("Turn off automatic recording file splitting for Perfect Comms stems. Deferred split packets cannot be confirmed individually.");
        return;
    }
    const char *path = obs_data_get_string(settings, "path");
    pcobs::Event event;
    event.kind = pcobs::EventKind::start;
    event.mode = bridge->mode();
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, event.path.data(), int(event.path.size()));
    obs_data_release(settings);
    if (!count) {
        timing_failure("OBS recording output has no supported local file path. Use standard encoded MKV or MP4 recording.");
        return;
    }
    bridge->post(event);
}
void detach_output()
{
    armed.store(false);
    if (!recording_output) return;
    obs_output_remove_packet_callback(recording_output, packet_clock, nullptr);
    signal_handler_disconnect(obs_output_get_signal_handler(recording_output), "activate", activated, nullptr);
    signal_handler_disconnect(obs_output_get_signal_handler(recording_output), "deactivate", deactivated, nullptr);
    signal_handler_disconnect(obs_output_get_signal_handler(recording_output), "stop", output_stopped, nullptr);
    obs_output_release(recording_output);
    recording_output = nullptr;
}
void frontend_event(obs_frontend_event event, void *)
{
    if (!bridge) return;
    if (event == OBS_FRONTEND_EVENT_RECORDING_STARTING) {
        detach_output();
        recording_output = obs_frontend_get_recording_output();
        if (!recording_output) { bridge->refuse("OBS recording output is unavailable."); return; }
        const auto flags = obs_output_get_flags(recording_output);
        const std::string_view id(obs_output_get_id(recording_output));
        if (!(flags & OBS_OUTPUT_ENCODED) || !(flags & OBS_OUTPUT_VIDEO) ||
            (id != "ffmpeg_muxer" && id != "mp4_output" && id != "mov_output")) {
            bridge->refuse("Voice stems require standard encoded OBS recording (MKV/MP4). Custom raw FFmpeg outputs are unsupported.");
            detach_output();
            return;
        }
        if (!bridge->source_ready()) {
            bridge->refuse("Add the Perfect Comms source once and keep its Sync Offset at 0 ms before recording.");
            detach_output();
            return;
        }
        pcobs::Event arm;
        arm.kind = pcobs::EventKind::arm;
        arm.mode = bridge->mode();
        bridge->post(arm);
        {
            std::lock_guard lock(packet_mutex);
            mux_headers_pending = id == "ffmpeg_muxer";
            normalize_presentation = id == "mp4_output" || id == "mov_output";
            // Hybrid muxers leave their final video packet queued, even during finalization.
            packets.reset(normalize_presentation);
            presentation_origin.reset();
        }
        capture_started.store(false);
        capture_finished.store(false);
        clock_failed.store(false);
        stop_signalled.store(false);
        armed.store(true);
        signal_handler_connect(obs_output_get_signal_handler(recording_output), "deactivate", deactivated, nullptr);
        signal_handler_connect(obs_output_get_signal_handler(recording_output), "stop", output_stopped, nullptr);
        signal_handler_connect(obs_output_get_signal_handler(recording_output), "activate", activated, nullptr);
        obs_output_add_packet_callback(recording_output, packet_clock, nullptr);
    } else if (event == OBS_FRONTEND_EVENT_RECORDING_STOPPED) {
        // Active captures finalize on deactivate, after encoder callbacks have returned.
        if (!capture_started.load() && !capture_finished.exchange(true)) {
            armed.store(false);
            pcobs::Event stop;
            stop.kind = pcobs::EventKind::stop;
            bridge->post(stop);
        }
    } else if (event == OBS_FRONTEND_EVENT_RECORDING_PAUSED || event == OBS_FRONTEND_EVENT_RECORDING_UNPAUSED) {
        pcobs::Event pause;
        pause.kind = event == OBS_FRONTEND_EVENT_RECORDING_PAUSED ? pcobs::EventKind::pause : pcobs::EventKind::resume;
        pause.clock.absolute = os_gettime_ns();
        obs_video_info video{};
        if (obs_get_video_info(&video) && video.fps_num)
            pause.clock.duration = (uint64_t(video.fps_den) * 2000000000ULL + video.fps_num - 1) / video.fps_num;
        bridge->post(pause);
    }
}
}
MODULE_EXPORT const char *obs_module_name(void) { return "Perfect Comms"; }
MODULE_EXPORT const char *obs_module_description(void) { return "Combined Perfect Comms voice audio and synchronized editable recording stems."; }
MODULE_EXPORT bool obs_module_load(void)
{
    try {
        bridge = std::make_unique<pcobs::Bridge>();
        obs_source_info source{};
        source.id = "perfect_comms_audio";
        source.type = OBS_SOURCE_TYPE_INPUT;
        source.output_flags = OBS_SOURCE_AUDIO | OBS_SOURCE_DO_NOT_DUPLICATE;
        source.get_name = source_name;
        source.create = source_create;
        source.destroy = source_destroy;
        source.get_properties = source_properties;
        obs_register_source(&source);
        obs_frontend_add_event_callback(frontend_event, nullptr);
        return true;
    } catch (const std::exception &error) {
        blog(LOG_ERROR, "[Perfect Comms] %s", error.what());
        bridge.reset();
        return false;
    }
}
MODULE_EXPORT void obs_module_unload(void)
{
    obs_frontend_remove_event_callback(frontend_event, nullptr);
    detach_output();
    bridge.reset();
}
