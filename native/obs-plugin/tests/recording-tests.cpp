#include "recording.hpp"
#include <windows.h>
#include <winioctl.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <fstream>
#include <iostream>
#include <iterator>
#include <stdexcept>

static void require(bool value, const char *message)
{
    if (!value) throw std::runtime_error(message);
}
static std::vector<uint8_t> read_file(const std::filesystem::path &path)
{
    std::ifstream file(path, std::ios::binary);
    return {std::istreambuf_iterator<char>(file), {}};
}
static float sample(const std::vector<uint8_t> &bytes, size_t frame)
{
    float value;
    std::memcpy(&value, bytes.data() + 80 + frame * 8, 4);
    return value;
}
template<class F> static void require_error(F operation, const char *message)
{
    bool rejected = false;
    try { operation(); } catch (const std::exception &) { rejected = true; }
    require(rejected, message);
}
static uint64_t little(const std::array<uint8_t, 80> &bytes, size_t offset, size_t count)
{
    uint64_t value = 0;
    for (size_t i = 0; i < count; ++i) value |= uint64_t(bytes[offset + i]) << (i * 8);
    return value;
}
static void timing_tests()
{
    const uint64_t origin = 1000000000;
    const auto fractional = pcobs::video_clock(1001, 1001, 60000, origin);
    require(fractional.relative == 16683333 && fractional.duration == 16683333, "fractional FPS uses OBS denominator ticks");
    const auto divided = pcobs::video_clock(2, 2, 60, origin);
    require(divided.relative == 33333333 && divided.duration == 33333333, "encoder divisor is not applied twice");
    const auto hybrid_first = pcobs::video_clock(4004, 1001, 60000, origin, 4004);
    require(hybrid_first.relative == 0 && hybrid_first.absolute == origin && hybrid_first.duration == 16683333,
        "hybrid muxer removes the initial presentation offset without shifting capture time");
    const auto hybrid_reordered = pcobs::video_clock(6006, 1001, 60000, origin + 33366667, 4004);
    require(hybrid_reordered.relative == 33366667, "hybrid reordered frames retain their normalized presentation time");
    require_error([&] { pcobs::video_clock(3003, 1001, 60000, origin, 4004); }, "presentation before hybrid origin is rejected");
    require_error([&] { pcobs::video_clock(0, 1, 60, 0); }, "missing CTS fails instead of guessing B-frame origin");
    require_error([&] { pcobs::video_clock(INT64_MAX, 1, 1, origin); }, "oversize timestamp rejected");
    pcobs::PacketAcceptance packets;
    require(!packets.resolve(10), "no pending packet");
    packets.submit(10, 100, pcobs::video_clock(2, 1, 60, origin + 33333333));
    const auto p_frame = packets.resolve(110);
    require(p_frame && p_frame->relative == 33333333, "accepted reordered P-frame retains PTS");
    packets.submit(110, 20, {});
    require(!packets.resolve(130), "accepted audio does not add a video interval");
    packets.submit(130, 100, pcobs::video_clock(0, 1, 60, origin));
    const auto b_frame = packets.resolve(230);
    require(b_frame && b_frame->relative == 0, "accepted B-frame can precede previous presentation time");
    packets.submit(230, 100, pcobs::video_clock(3, 1, 60, origin + 50000000));
    require(!packets.resolve(230, true), "stop-triggering rejected video is excluded");
    packets.submit(230, 20, {});
    require_error([&] { packets.resolve(231); }, "unconfirmed counter change fails visibly");
    packets.reset();
    packets.submit(230, 100, fractional);
    require(packets.resolve(330, true).has_value(), "last accepted packet commits after encoder teardown");
    packets.submit(0, 100, fractional, 47);
    require(packets.resolve(147).has_value(), "initial muxer encoder headers do not hide an accepted video packet");
    packets.submit(0, 100, fractional, 47);
    require(!packets.resolve(47, true), "initial headers alone do not commit a stop-rejected video packet");
    packets.reset(true);
    packets.submit(0, 100, pcobs::Clock{origin, 0, 20000000}, 0, pcobs::VideoDecodeTime{0, 50});
    require(!packets.resolve(100), "hybrid first video remains buffered until another video is accepted");
    packets.submit(100, 20, {});
    require(!packets.resolve(120), "accepted hybrid audio does not release buffered video");
    packets.submit(120, 100, pcobs::Clock{origin + 80000000, 80000000, 20000000}, 0, pcobs::VideoDecodeTime{1, 50});
    const auto first_hybrid = packets.resolve(220);
    require(first_hybrid && first_hybrid->relative == 0, "next accepted hybrid video releases preceding sample");
    packets.submit(220, 100, pcobs::Clock{origin + 20000000, 20000000, 20000000}, 0, pcobs::VideoDecodeTime{2, 50});
    const auto reordered_hybrid = packets.resolve(320);
    require(reordered_hybrid && reordered_hybrid->relative == 80000000, "hybrid commit delay preserves decode-order PTS reordering");
    packets.submit(320, 100, pcobs::Clock{origin + 160000000, 160000000, 20000000}, 0, pcobs::VideoDecodeTime{3, 50});
    const auto final_hybrid = packets.resolve(420, true);
    require(final_hybrid && final_hybrid->relative == 20000000, "final accepted hybrid video releases only its preceding sample");
    require(!packets.resolve(420, true), "hybrid final queued sample is discarded exactly once");
    packets.reset(true);
    packets.submit(0, 100, fractional, 0, pcobs::VideoDecodeTime{0, 60000});
    require(!packets.resolve(100), "single hybrid video stays buffered");
    packets.submit(100, 100, hybrid_reordered, 0, pcobs::VideoDecodeTime{1001, 60000});
    require(!packets.resolve(100, true), "stop-rejected hybrid video does not release the last queued sample");
    packets.reset(true);
    packets.submit(0, 100, fractional, 0, pcobs::VideoDecodeTime{0, 60000});
    require(!packets.resolve(100), "hybrid buffered state starts empty after reset");
    packets.submit(100, 20, {});
    require(!packets.resolve(120, true) && !packets.resolve(120, true), "final hybrid audio drops rather than commits held video");
    packets.reset(true);
    packets.submit(0, 100, fractional, 0, pcobs::VideoDecodeTime{0, 60000});
    require_error([&] { packets.resolve(99); }, "hybrid acceptance still rejects unexpected byte accounting");
    packets.reset(true);
    packets.submit(0, 100, fractional, 0, pcobs::VideoDecodeTime{0, 60000});
    require(!packets.resolve(100), "hybrid reset fixture holds a video sample");
    packets.reset(true);
    packets.submit(0, 100, hybrid_first, 0, pcobs::VideoDecodeTime{0, 60000});
    require(!packets.resolve(100), "new recording reset discards the previous recording's buffered sample");
    require(!packets.resolve(100, true), "finalization without a pending packet discards held hybrid video");
    packets.reset();
    packets.submit(0, 100, fractional);
    require(packets.resolve(100, true).has_value(), "ordinary muxer reset disables hybrid buffering");
    packets.reset(true);
    require_error([&] { packets.submit(0, 100, fractional); }, "hybrid video requires actual decode timing");
    require_error([&] { packets.submit(0, 100, fractional, 0, pcobs::VideoDecodeTime{0, 0}); }, "hybrid decode timebase must be valid");
    packets.submit(0, 100, hybrid_first, 0, pcobs::VideoDecodeTime{-1001, 60000});
    require(!packets.resolve(100), "negative initial DTS is valid and remains buffered");
    packets.submit(100, 100, hybrid_reordered, 0, pcobs::VideoDecodeTime{3003, 60000});
    const auto stretched_fractional = packets.resolve(200, true);
    require(stretched_fractional && stretched_fractional->duration == 66733333,
        "hybrid duration converts the raw fractional DTS delta before rounding");
    for (const auto next : {pcobs::VideoDecodeTime{10, 50}, pcobs::VideoDecodeTime{9, 50},
                            pcobs::VideoDecodeTime{11, 60}, pcobs::VideoDecodeTime{61, 50}}) {
        packets.reset(true);
        packets.submit(0, 100, hybrid_first, 0, pcobs::VideoDecodeTime{10, 50});
        require(!packets.resolve(100), "decode timing validation fixture buffers first sample");
        packets.submit(100, 100, hybrid_reordered, 0, next);
        require_error([&] { packets.resolve(200); }, "non-increasing, changed-timebase or excessive DTS delta fails visibly");
    }
    pcobs::CaptureDrain drain;
    drain.start(origin + 20000000, origin);
    require(!drain.ready(origin + 19999999), "drain waits for the whole accepted interval");
    require(drain.ready(origin + 20000000), "next combined start acknowledges prior stems");
    drain.expire(origin + 9999999999ULL);
    require_error([&] { drain.expire(origin + 10000000000ULL); }, "missing watermark has bounded visible failure");
}
struct Junction {
    std::filesystem::path path;
    Junction(const std::filesystem::path &link, const std::filesystem::path &target) : path(link)
    {
        const auto printable = std::filesystem::absolute(target).wstring();
        const auto substitute = LR"(\??\)" + printable;
        struct {
            DWORD tag = IO_REPARSE_TAG_MOUNT_POINT;
            WORD length = 0;
            WORD reserved = 0;
            WORD substitute_offset = 0;
            WORD substitute_length = 0;
            WORD print_offset = 0;
            WORD print_length = 0;
            wchar_t paths[4096]{};
        } buffer;
        require(substitute.size() + printable.size() + 2 <= std::size(buffer.paths), "junction target fits reparse buffer");
        buffer.substitute_length = WORD(substitute.size() * sizeof(wchar_t));
        buffer.print_offset = WORD(buffer.substitute_length + sizeof(wchar_t));
        buffer.print_length = WORD(printable.size() * sizeof(wchar_t));
        buffer.length = WORD(8 + buffer.print_offset + buffer.print_length + sizeof(wchar_t));
        std::copy(substitute.begin(), substitute.end(), buffer.paths);
        std::copy(printable.begin(), printable.end(), buffer.paths + buffer.print_offset / sizeof(wchar_t));
        require(std::filesystem::create_directory(path), "junction directory created");
        const auto directory = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (directory == INVALID_HANDLE_VALUE) {
            RemoveDirectoryW(path.c_str());
            throw std::runtime_error("junction directory opened for reparse setup");
        }
        DWORD transferred = 0;
        const auto created = DeviceIoControl(directory, FSCTL_SET_REPARSE_POINT, &buffer, DWORD(8 + buffer.length),
            nullptr, 0, &transferred, nullptr);
        CloseHandle(directory);
        if (!created) RemoveDirectoryW(path.c_str());
        require(created != FALSE, "junction created without symbolic-link privilege");
    }
    ~Junction() { RemoveDirectoryW(path.c_str()); }
    Junction(const Junction &) = delete;
    Junction &operator=(const Junction &) = delete;
};
static void require_directory_released(const std::filesystem::path &directory)
{
    const auto writable = CreateFileW(directory.c_str(), GENERIC_WRITE | DELETE,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    require(writable != INVALID_HANDLE_VALUE, "released stem directory permits write and delete access");
    CloseHandle(writable);
    const auto renamed = std::filesystem::path(directory.wstring() + L"-released");
    std::filesystem::rename(directory, renamed);
    std::filesystem::rename(renamed, directory);
}
static void directory_containment_tests(const std::filesystem::path &root)
{
    const auto occupied_file = root / L"collision-voices";
    const auto occupied_directory = root / L"collision-voices-1";
    std::ofstream(occupied_file, std::ios::binary) << "existing file";
    require(std::filesystem::create_directory(occupied_directory), "occupied collision directory created");
    std::ofstream(occupied_directory / L"sentinel", std::ios::binary) << "existing directory";
    const auto file_before = read_file(occupied_file);
    const auto directory_before = read_file(occupied_directory / L"sentinel");
    require(!file_before.empty() && !directory_before.empty(), "collision sentinels written");
    pcobs::Recorder recorder;
    recorder.start(root / L"collision.mkv", 0);
    require(recorder.directory() == root / L"collision-voices-2", "ordinary file and directory collisions select exact next suffix");
    recorder.stop();
    require(read_file(occupied_file) == file_before && read_file(occupied_directory / L"sentinel") == directory_before,
        "collision files remain unchanged");
    require_directory_released(recorder.directory());

    const auto target = root / L"junction-target";
    require(std::filesystem::create_directory(target), "junction target created");
    std::ofstream(target / L"sentinel", std::ios::binary) << "junction target";
    const auto target_before = read_file(target / L"sentinel");
    require(!target_before.empty(), "junction sentinel written");
    {
        Junction parent(root / L"junction-parent", target);
        require_error([&] { recorder.start(parent.path / L"parent.mkv", 0); }, "parent junction is rejected");
        require(!recorder.active(), "parent junction failure leaves recorder inactive");
        require(!std::filesystem::exists(target / L"parent-voices"), "parent junction target receives no stem directory");
    }
    {
        Junction leaf(root / L"junction-leaf-voices", target);
        bool rejected = false;
        try { recorder.start(root / L"junction-leaf.mkv", 0); } catch (const std::exception &) { rejected = true; }
        if (!rejected) {
            require(recorder.directory() == root / L"junction-leaf-voices-1", "leaf junction collision selects next suffix");
            recorder.clock({1000000000, 0, 20000000});
            pcobs::Audio audio;
            audio.timestamp = 1000000000;
            audio.connection = 1;
            audio.track = 1;
            audio.frames = 960;
            audio.samples.fill(0.25f);
            recorder.audio(audio);
            const auto directory = recorder.directory();
            recorder.stop();
            require(std::filesystem::exists(directory / L"Private voices.wav"), "leaf junction collision records into new directory");
        }
        require(!recorder.active(), "leaf junction never leaves an unclosed recording");
        require(!std::filesystem::exists(target / L"Private voices.wav"), "leaf junction target receives no WAV");
    }
    require(read_file(target / L"sentinel") == target_before &&
        std::distance(std::filesystem::directory_iterator(target), std::filesystem::directory_iterator{}) == 1,
        "junction target contents remain unchanged");

    recorder.start(root / L"abandoned-directory.mkv", 0);
    const auto abandoned = recorder.directory();
    recorder.abandon();
    require_directory_released(abandoned);
    std::filesystem::path destroyed;
    {
        pcobs::Recorder scoped;
        scoped.start(root / L"destroyed-directory.mkv", 0);
        destroyed = scoped.directory();
    }
    require_directory_released(destroyed);
}
static void video_gap_tests(const std::filesystem::path &root)
{
    constexpr uint64_t origin = 1000000000;
    constexpr uint64_t step = 20000000;
    pcobs::Audio audio;
    audio.connection = 1;
    audio.track = 1;
    audio.frames = 960;
    audio.samples.fill(0.25f);
    pcobs::Recorder recorder;
    recorder.start(root / L"video-gaps.mkv", 0);
    const auto directory = recorder.directory();
    for (unsigned index = 0; index < 5; ++index) {
        audio.timestamp = origin + index * step;
        recorder.audio(audio);
    }
    recorder.clock({origin, 0, step});
    recorder.clock({origin + 4 * step, 4 * step, step});
    audio.timestamp = origin + 5 * step;
    recorder.audio(audio);
    recorder.clock({origin + 6 * step, 6 * step, step});
    audio.timestamp = origin + 6 * step;
    recorder.audio(audio);
    for (unsigned index : {3u, 1u, 2u, 5u}) recorder.clock({origin + index * step, index * step, step});
    recorder.stop();
    const auto bytes = read_file(directory / L"Private voices.wav");
    require(bytes.size() == 80 + 7 * 960 * 8, "dropped video frames retain the complete confirmed output duration");
    for (size_t frame = 0; frame < 7 * 960; ++frame)
        require(sample(bytes, frame) == 0.25f, "continuous source audio spans video gaps without silence or duplicate B-frame writes");

    recorder.start(root / L"pause-gap.mkv", 0);
    const auto paused_directory = recorder.directory();
    recorder.clock({origin, 0, step});
    audio.timestamp = origin;
    recorder.audio(audio);
    recorder.pause(origin + step);
    recorder.resume(origin + 4 * step);
    audio.timestamp = origin + step;
    audio.samples.fill(1.0f);
    recorder.audio(audio);
    recorder.clock({origin + 4 * step, step, step});
    audio.timestamp = origin + 4 * step;
    audio.samples.fill(0.25f);
    recorder.audio(audio);
    recorder.stop();
    const auto paused = read_file(paused_directory / L"Private voices.wav");
    require(paused.size() == 80 + 2 * 960 * 8, "pause compacts the output instead of filling its wall-clock gap");
    for (size_t frame = 0; frame < 2 * 960; ++frame)
        require(sample(paused, frame) == 0.25f, "delayed audio inside a pause never bridges discontinuous output clocks");

    recorder.start(root / L"retained-gap.mkv", 0);
    const auto retained_directory = recorder.directory();
    recorder.clock({origin, 0, step});
    recorder.clock({origin + 10 * step, 10 * step, step});
    recorder.expire(origin + 10000000000ULL + 5 * step);
    audio.timestamp = origin + 6 * step;
    recorder.audio(audio);
    recorder.stop();
    const auto retained = read_file(retained_directory / L"Private voices.wav");
    require(retained.size() == 80 + 11 * 960 * 8, "expiry retains the confirmed output duration");
    require(sample(retained, 6 * 960 - 1) == 0.0f && sample(retained, 6 * 960) == 0.25f &&
        sample(retained, 7 * 960 - 1) == 0.25f && sample(retained, 7 * 960) == 0.0f,
        "retained audio inside a video gap keeps its preceding clock anchor after expiry");
}
static void fractional_video_gap_tests(const std::filesystem::path &root)
{
    constexpr uint64_t origin = 3170689201005;
    pcobs::Recorder recorder;
    recorder.start(root / L"fractional-gap.mp4", 0);
    const auto directory = recorder.directory();
    pcobs::Audio audio;
    audio.connection = 1;
    audio.track = 1;
    audio.frames = 960;
    audio.timestamp = origin + 20000000;
    audio.samples.fill(0.25f);
    recorder.audio(audio);
    recorder.clock({origin, 0, 16683333});
    recorder.clock({origin + 66733332, 66733333, 16683333});
    recorder.stop();
    const auto wave = read_file(directory / L"Private voices.wav");
    require(wave.size() == 80 + 4004 * 8, "fractional video gap retains normalized media duration");
    size_t audible = 0;
    for (size_t frame = 0; frame < 4004; ++frame) {
        const auto value = sample(wave, frame);
        require(value == 0.0f || value == 0.25f, "fractional gap contains only the supplied source samples");
        audible += value != 0.0f;
    }
    require(audible == 960 && sample(wave, 1200) == 0.25f && sample(wave, 1800) == 0.25f,
        "actual OBS CTS floor drift of one nanosecond does not silence a continuous fractional-FPS video gap");

    const pcobs::Clock earlier{origin + 33366666, 33366667, 33366667};
    const pcobs::Clock later{origin + 50049999, 50050000, 33366667};
    for (unsigned order = 0; order < 3; ++order) {
        recorder.start(root / (L"fractional-overlap-" + std::to_wstring(order) + L".mp4"), 0);
        const auto overlap_directory = recorder.directory();
        audio.timestamp = origin + 60000000;
        if (order != 2) recorder.audio(audio);
        recorder.clock(order == 1 ? later : earlier);
        recorder.clock(order == 1 ? earlier : later);
        if (order == 2) recorder.audio(audio);
        recorder.stop();
        const auto overlap = read_file(overlap_directory / L"Private voices.wav");
        require(overlap.size() == 80 + 4004 * 8, "fractional stretched overlap retains media duration");
        size_t overlap_audible = 0;
        for (size_t frame = 0; frame < 4004; ++frame) {
            const auto value = sample(overlap, frame);
            require(value == 0.0f || value == 0.25f, "fractional overlapping clocks do not duplicate source samples");
            overlap_audible += value != 0.0f;
        }
        require(overlap_audible == 960, "actual fractional sample phases preserve all audio across a stretched overlap");
    }
    auto boundary_audio = audio;
    boundary_audio.timestamp = origin + 40000000;
    for (size_t index = 0; index < boundary_audio.frames; ++index) {
        boundary_audio.samples[index * 2] = float(index + 1) / 4096.0f;
        boundary_audio.samples[index * 2 + 1] = -boundary_audio.samples[index * 2];
    }
    for (unsigned order = 0; order < 4; ++order) {
        recorder.start(root / (L"fractional-boundary-" + std::to_wstring(order) + L".mp4"), 0);
        const auto boundary_directory = recorder.directory();
        recorder.clock({origin, 0, 16683333});
        if (order < 2) recorder.audio(boundary_audio);
        const auto &first_clock = order % 2 ? later : earlier;
        const auto &second_clock = order % 2 ? earlier : later;
        recorder.clock({first_clock.absolute, first_clock.relative, 16683333});
        recorder.clock({second_clock.absolute, second_clock.relative, 16683333});
        if (order >= 2) recorder.audio(boundary_audio);
        recorder.stop();
        const auto boundary = read_file(boundary_directory / L"Private voices.wav");
        require(boundary.size() == 80 + 3203 * 8, "fractional boundary preserves the video duration");
        for (size_t frame = 0; frame < 3203; ++frame) {
            const auto expected = frame >= 1920 && frame < 2880 ? float(frame - 1919) / 4096.0f : 0.0f;
            float right;
            std::memcpy(&right, boundary.data() + 80 + frame * 8 + 4, sizeof(right));
            require(sample(boundary, frame) == expected && right == -expected,
                "fractional clock ownership preserves every distinguishable stereo sample in order");
        }
    }
    for (unsigned late_audio = 0; late_audio < 2; ++late_audio) {
        recorder.start(root / (L"fractional-expiry-" + std::to_wstring(late_audio) + L".mp4"), 0);
        const auto expiry_directory = recorder.directory();
        recorder.clock({origin, 0, 16683333});
        recorder.clock({earlier.absolute, earlier.relative, 16683333});
        recorder.expire(origin + 11000000000ULL);
        boundary_audio.timestamp = origin + 12040000000ULL;
        if (!late_audio) recorder.audio(boundary_audio);
        recorder.clock({earlier.absolute + 12000000000ULL, earlier.relative + 12000000000ULL, 16683333});
        recorder.clock({later.absolute + 12000000000ULL, later.relative + 12000000000ULL, 16683333});
        if (late_audio) recorder.audio(boundary_audio);
        recorder.stop();
        const auto expiry = read_file(expiry_directory / L"Private voices.wav");
        require(expiry.size() == 80 + 579203 * 8, "expired clock history preserves final media duration");
        for (size_t index = 0; index < boundary_audio.frames; ++index) {
            float right;
            std::memcpy(&right, expiry.data() + 80 + (577920 + index) * 8 + 4, sizeof(right));
            require(sample(expiry, 577920 + index) == boundary_audio.samples[index * 2] &&
                right == boundary_audio.samples[index * 2 + 1],
                "retained predecessor preserves stereo sample phase after clock history expires");
        }
    }
    recorder.start(root / L"fractional-contradictory-overlap.mp4", 0);
    recorder.clock(earlier);
    require_error([&] { recorder.clock({later.absolute, later.relative + 41667, later.duration}); },
        "fractional overlap with contradictory multi-sample elapsed timing is rejected");
    recorder.stop();

    constexpr uint64_t step = 20000000;
    recorder.start(root / L"quantized-pause-gap.mp4", 0);
    const auto paused_directory = recorder.directory();
    recorder.clock({origin, 0, step});
    audio.timestamp = origin;
    recorder.audio(audio);
    recorder.pause(origin + step);
    recorder.resume(origin + step + 1);
    audio.timestamp = origin + 2 * step;
    audio.samples.fill(1.0f);
    recorder.audio(audio);
    recorder.clock({origin + 4 * step + 1, 4 * step, step});
    audio.timestamp = origin + 4 * step + 1;
    audio.samples.fill(0.25f);
    recorder.audio(audio);
    recorder.stop();
    const auto paused = read_file(paused_directory / L"Private voices.wav");
    require(paused.size() == 80 + 5 * 960 * 8 && sample(paused, 0) == 0.25f &&
        sample(paused, 2400) == 0.0f && sample(paused, 4300) == 0.25f,
        "explicit pause boundary prevents bridging even when clock deltas quantize to the same sample duration");

    recorder.start(root / L"delayed-pre-pause-clock.mp4", 0);
    const auto delayed_directory = recorder.directory();
    recorder.clock({origin, 0, step});
    recorder.pause(origin + 5 * step);
    recorder.resume(origin + 10 * step);
    audio.timestamp = origin + 2 * step;
    recorder.audio(audio);
    recorder.clock({origin + 4 * step, 4 * step, step});
    recorder.stop();
    const auto delayed = read_file(delayed_directory / L"Private voices.wav");
    require(delayed.size() == 80 + 5 * 960 * 8 && sample(delayed, 2400) == 0.25f,
        "delayed pre-pause clocks can bridge their original epoch after resume");
}
static void hybrid_tail_tests(const std::filesystem::path &root)
{
    constexpr uint64_t origin = 1000000000;
    constexpr uint64_t step = 20000000;
    pcobs::Recorder recorder;
    recorder.start(root / L"hybrid-tail.mp4", 0);
    const auto directory = recorder.directory();
    pcobs::Audio audio;
    audio.connection = 1;
    audio.track = 1;
    audio.frames = 960;
    audio.samples.fill(0.25f);
    for (unsigned index = 0; index < 9; ++index) {
        audio.timestamp = origin + index * step;
        recorder.audio(audio);
    }
    pcobs::PacketAcceptance packets;
    packets.reset(true);
    uint64_t bytes = 0;
    int64_t decode = 0;
    const auto video = [&](unsigned index, bool finishing = false) {
        packets.submit(bytes, 100, pcobs::Clock{origin + index * step, index * step, step}, 0,
            pcobs::VideoDecodeTime{decode++, 50});
        bytes += 100;
        if (const auto clock = packets.resolve(bytes, finishing)) recorder.clock(*clock);
    };
    video(0);
    packets.submit(bytes, 20, {});
    bytes += 20;
    require(!packets.resolve(bytes), "interleaved audio cannot commit hybrid tail video");
    for (unsigned index : {4u, 1u, 2u, 3u}) video(index);
    video(8, true);
    recorder.stop();
    const auto wave = read_file(directory / L"Private voices.wav");
    require(wave.size() == 80 + 5 * 960 * 8, "WAV ends at last muxed presentation sample, not the discarded future P-frame");
    for (size_t frame = 0; frame < 5 * 960; ++frame)
        require(sample(wave, frame) == 0.25f, "hybrid committed samples preserve continuous source audio");
}
static void hybrid_stretched_sample_tests(const std::filesystem::path &root)
{
    constexpr uint64_t origin = 1000000000;
    constexpr uint64_t step = 20000000;
    pcobs::Audio audio;
    audio.connection = 1;
    audio.track = 1;
    audio.frames = 960;
    audio.samples.fill(0.25f);
    pcobs::Recorder recorder;
    pcobs::PacketAcceptance packets;
    uint64_t bytes = 0;
    const auto video = [&](unsigned presentation, int64_t decode, bool finishing = false) {
        packets.submit(bytes, 100, pcobs::Clock{origin + presentation * step, presentation * step, step}, 0,
            pcobs::VideoDecodeTime{decode, 50});
        bytes += 100;
        if (const auto clock = packets.resolve(bytes, finishing)) recorder.clock(*clock);
    };
    recorder.start(root / L"hybrid-stretched-tail.mp4", 0);
    const auto directory = recorder.directory();
    for (unsigned index = 0; index < 3; ++index) {
        audio.timestamp = origin + index * step;
        recorder.audio(audio);
    }
    packets.reset(true);
    video(0, 0);
    packets.submit(bytes, 20, {});
    bytes += 20;
    require(!packets.resolve(bytes), "audio cannot supply the missing hybrid sample duration");
    video(2, 2, true);
    recorder.stop();
    const auto wave = read_file(directory / L"Private voices.wav");
    require(wave.size() == 80 + 2 * 960 * 8, "final emitted hybrid sample spans its actual two-tick DTS duration");
    for (size_t frame = 0; frame < 2 * 960; ++frame)
        require(sample(wave, frame) == 0.25f, "dropped frame before held hybrid tail does not truncate voice");

    recorder.start(root / L"hybrid-reordered-drop.mp4", 0);
    const auto reordered_directory = recorder.directory();
    for (unsigned index = 0; index < 9; ++index) {
        audio.timestamp = origin + index * step;
        recorder.audio(audio);
    }
    packets.reset(true);
    bytes = 0;
    video(0, -2);
    video(4, -1);
    video(1, 1);
    video(2, 2);
    video(3, 3);
    video(8, 5, true);
    recorder.stop();
    const auto reordered = read_file(reordered_directory / L"Private voices.wav");
    require(reordered.size() == 80 + 6 * 960 * 8, "reordered dropped-frame durations use DTS rather than PTS deltas");
    for (size_t frame = 0; frame < 6 * 960; ++frame)
        require(sample(reordered, frame) == 0.25f, "overlapping stretched B-frame intervals neither silence nor duplicate voice");

    recorder.start(root / L"contradictory-overlap.mp4", 0);
    recorder.clock({origin, 0, 2 * step});
    require_error([&] { recorder.clock({origin + step, 2 * step, 2 * step}); },
        "overlapping clocks with different audio mapping are rejected");
    recorder.stop();
    recorder.start(root / L"paused-overlap.mp4", 0);
    recorder.clock({origin, 0, 2 * step});
    recorder.pause(origin + step);
    recorder.resume(origin + step + step / 2);
    require_error([&] { recorder.clock({origin + step, step, 2 * step}); },
        "consistent overlap cannot cross an explicit resume boundary");
    recorder.stop();
}
static void pause_and_storage_tests(const std::filesystem::path &root)
{
    const uint64_t origin = 1000000000;
    const uint64_t resumed = origin + 3600000000000ULL;
    pcobs::Recorder recorder;
    recorder.start(root / L"long-pause.mkv", 0);
    const auto directory = recorder.directory();
    const auto writable = CreateFileW(directory.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    const auto write_error = GetLastError();
    if (writable != INVALID_HANDLE_VALUE) CloseHandle(writable);
    require(writable == INVALID_HANDLE_VALUE && write_error == ERROR_SHARING_VIOLATION,
        "retained stem directory denies a reparse writer through sharing protection");
    recorder.clock({origin, 0, 20000000});
    pcobs::Audio audio;
    audio.timestamp = origin + 10000000;
    audio.connection = 1;
    audio.track = 1;
    audio.group = 0;
    audio.frames = 960;
    audio.samples.fill(0.25f);
    recorder.audio(audio);
    recorder.pause(origin + 80000000);
    for (unsigned i = 0; i < 10000; ++i) {
        audio.timestamp = origin + 80000000 + uint64_t(i) * 20000000;
        recorder.audio(audio);
    }
    recorder.expire(resumed);
    recorder.resume(resumed);
    recorder.expire(resumed);
    recorder.clock({origin + 20000000, 20000000, 20000000});
    recorder.clock({resumed, 40000000, 20000000});
    audio.timestamp = resumed;
    recorder.audio(audio);
    recorder.stop();
    const auto bytes = read_file(directory / L"Private voices.wav");
    require(bytes.size() == 80 + 2880 * 8, "hour-long pause contributes no timeline duration");
    require(sample(bytes, 480) == 0.25f && sample(bytes, 960) == 0.25f, "pre-pause encoder tail survives wall-clock expiry");
    require(sample(bytes, 1920) == 0.25f, "resumed audio maps to compact output timeline");

    recorder.start(root / L"sparse-rf64.mkv", 0);
    const auto sparse_directory = recorder.directory();
    recorder.clock({origin, 0, 20000000});
    audio.timestamp = origin;
    recorder.audio(audio);
    constexpr uint64_t final_frames = 600000000;
    recorder.clock({origin + 12500000000000ULL - 20000000, 12500000000000ULL - 20000000, 20000000});
    recorder.stop();
    const auto path = sparse_directory / L"Private voices.wav";
    require(std::filesystem::file_size(path) == 80 + final_frames * 8, "RF64 file extends sparsely to exact output duration");
    std::array<uint8_t, 80> header{};
    std::ifstream file(path, std::ios::binary);
    file.read(reinterpret_cast<char *>(header.data()), header.size());
    require(file.gcount() == header.size(), "RF64 header readable");
    require(std::memcmp(header.data(), "RF64", 4) == 0 && std::memcmp(header.data() + 12, "ds64", 4) == 0, "existing RIFF converted to RF64");
    require(little(header, 20, 8) == final_frames * 8 + 72 && little(header, 28, 8) == final_frames * 8 && little(header, 36, 8) == final_frames, "RF64 sizes and sample count");
    DWORD high = 0;
    const DWORD low = GetCompressedFileSizeW(path.c_str(), &high);
    require((uint64_t(high) << 32 | low) < 16 * 1024 * 1024, "multi-gigabyte silence does not allocate multi-gigabyte storage");

    HANDLE shutdown = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    require(shutdown != nullptr, "cancellation event");
    {
        pcobs::Recorder cancellable(shutdown);
        cancellable.start(root / L"cancelled.mkv", 0);
        cancellable.clock({origin, 0, 20000000});
        cancellable.audio(audio);
        SetEvent(shutdown);
        require_error([&] { cancellable.stop(); }, "shutdown prevents starting finalization I/O");
        cancellable.abandon();
    }
    CloseHandle(shutdown);
}
int main()
{
    try {
        timing_tests();
        require(pcobs::frames_at(1000000000) == 48000, "sample clock");
        const auto small = pcobs::wave_header(48000);
        const auto large = pcobs::wave_header(600000000);
        require(std::memcmp(small.data(), "RIFF", 4) == 0, "RIFF header");
        require(std::memcmp(large.data(), "RF64", 4) == 0, "RF64 header");
        require(std::memcmp(large.data() + 12, "ds64", 4) == 0, "RF64 ds64");
        require(pcobs::safe_name("../CON:bad\\name") == L"___CON_bad_name", "unsafe filename");
        require(pcobs::safe_name("CON") == L"_CON", "device filename");
        require(pcobs::safe_name("LPT1.wav") == L"_LPT1.wav", "device extension");
        require(pcobs::safe_name("\xc0\xaf") == L"Voice", "invalid UTF8");
        pcobs::Hello hello;
        pcobs::Audio audio;
        const std::array<uint8_t, 12> greeting{1,1,0,0,0,0,0x80,0xbb,0,0,2,0};
        require(pcobs::parse_message(greeting, hello, audio), "HELLO packet");
        auto bad = greeting;
        bad[5] = 2;
        require(!pcobs::parse_message(bad, hello, audio), "invalid mode rejected");
        require(!pcobs::parse_message({}, hello, audio), "empty message rejected");
        std::vector<uint8_t> packet(26, 0);
        packet[0] = 2;
        packet[1] = 1;
        packet[9] = 1;
        packet[16] = 1;
        require(pcobs::parse_message(packet, hello, audio), "AUDIO packet");
        packet[25] = 0x7f;
        packet[24] = 0x80;
        require(!pcobs::parse_message(packet, hello, audio), "nonfinite PCM rejected");

        wchar_t temporary[MAX_PATH];
        GetTempPathW(MAX_PATH, temporary);
        const auto root = std::filesystem::path(temporary) / (L"pcobs-tests-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
        std::filesystem::create_directory(root);
        struct Cleanup { std::filesystem::path path; ~Cleanup() { std::error_code ec; std::filesystem::remove_all(path, ec); } } cleanup{root};
        pcobs::Recorder recorder;
        recorder.start(root / L"video.mkv", 0);
        const auto directory = recorder.directory();
        const uint64_t origin = 1000000000;
        recorder.clock({origin, 0, 20000000});
        recorder.clock({origin + 40000000, 20000000, 20000000});
        audio = {};
        audio.timestamp = origin + 10000000;
        audio.connection = 1;
        audio.track = 1;
        audio.group = 1;
        audio.frames = 960;
        std::memcpy(audio.name.data(), "Alias", 5);
        audio.samples.fill(0.25f);
        recorder.audio(audio);
        recorder.audio(audio);
        audio.timestamp = origin + 40000000;
        audio.connection = 2;
        recorder.audio(audio);
        recorder.stop();
        std::vector<std::filesystem::path> files;
        for (const auto &entry : std::filesystem::directory_iterator(directory)) files.push_back(entry.path());
        require(files.size() == 2, "connection namespaces distinct");
        std::sort(files.begin(), files.end());
        const auto first = read_file(files[0]);
        const auto second = read_file(files[1]);
        require(first.size() == 80 + 1920 * 8, "duration equals output clock");
        require(second.size() == first.size(), "latejoin leading silence and equal duration");
        require(sample(first, 0) == 0.0f && sample(first, 480) == 0.5f, "leading silence and overlap addition");
        require(sample(first, 960) == 0.0f, "paused audio excluded");
        require(sample(second, 0) == 0.0f && sample(second, 960) == 0.25f, "resume aligned");
        recorder.start(root / L"video.mkv", 1);
        require(recorder.directory() != directory, "exclusive folder never overwritten");
        recorder.clock({origin, 0, 20000000});
        audio.timestamp = origin;
        audio.connection = 3;
        audio.group = 0;
        recorder.audio(audio);
        audio.track = 2;
        recorder.audio(audio);
        const auto private_directory = recorder.directory();
        recorder.stop();
        const auto private_wave = read_file(private_directory / L"Private voices.wav");
        require(sample(private_wave, 0) == 0.5f, "private identities collapse");
        recorder.start(root / L"chronology.mkv", 0);
        recorder.audio(audio);
        audio.timestamp--;
        bool rejected = false;
        try { recorder.audio(audio); } catch (const std::exception &) { rejected = true; }
        require(rejected, "backward timestamp rejected");
        recorder.stop();
        directory_containment_tests(root);
        video_gap_tests(root);
        fractional_video_gap_tests(root);
        hybrid_tail_tests(root);
        hybrid_stretched_sample_tests(root);
        pause_and_storage_tests(root);
        std::cout << "recording behavior passed\n";
        return 0;
    } catch (const std::exception &error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
