#pragma once
#include <array>
#include <bitset>
#include <cstdint>
#include <deque>
#include <filesystem>
#include <functional>
#include <memory>
#include <optional>
#include <windows.h>
#include <span>
#include <string>
#include <vector>

namespace pcobs {
constexpr uint32_t sample_rate = 48000;
constexpr size_t max_frames = 960;
constexpr size_t max_tracks = 256;
constexpr size_t max_message = 262144;
struct Audio {
    uint64_t timestamp = 0;
    uint64_t connection = 0;
    uint32_t track = 0;
    uint16_t frames = 0;
    uint8_t group = 0;
    std::array<char, 193> name{};
    std::array<float, max_frames * 2> samples{};
};
struct Hello { uint8_t mode = 0; };
enum class EventKind { audio, clock, arm, start, stop, pause, resume, hello, disconnected, watermark, failure };
struct Clock {
    uint64_t absolute = 0;
    uint64_t relative = 0;
    uint64_t duration = 0;
};
struct Event {
    EventKind kind = EventKind::audio;
    Audio audio{};
    Clock clock{};
    uint8_t mode = 0;
    std::array<char, 256> message{};
    std::array<wchar_t, 1024> path{};
};
bool parse_message(std::span<const uint8_t> bytes, Hello &hello, Audio &audio);
std::wstring safe_name(std::string_view name);
uint64_t frames_at(uint64_t ns);
std::array<uint8_t, 80> wave_header(uint64_t frames);
Clock video_clock(int64_t pts, int32_t numerator, int32_t denominator, uint64_t composition, int64_t presentation_origin = 0);
struct VideoDecodeTime {
    int64_t ticks = 0;
    int32_t denominator = 0;
};
class PacketAcceptance {
public:
    std::optional<Clock> resolve(uint64_t total_bytes, bool finishing = false);
    void submit(uint64_t total_bytes, uint64_t size, std::optional<Clock> clock, uint64_t header_bytes = 0,
                std::optional<VideoDecodeTime> decode = {});
    void reset(bool buffered_video = false) noexcept;
private:
    bool pending_ = false;
    bool buffered_video_ = false;
    std::optional<Clock> buffered_clock_;
    std::optional<VideoDecodeTime> buffered_decode_;
    std::optional<VideoDecodeTime> decode_;
    uint64_t before_ = 0;
    uint64_t size_ = 0;
    std::optional<Clock> clock_;
};
class CaptureDrain {
public:
    void start(uint64_t end, uint64_t now);
    bool ready(uint64_t watermark) const noexcept;
    void expire(uint64_t now) const;
private:
    uint64_t end_ = 0;
    uint64_t deadline_ = 0;
};

class Recorder {
public:
    explicit Recorder(HANDLE shutdown = nullptr);
    ~Recorder();
    Recorder(const Recorder &) = delete;
    Recorder &operator=(const Recorder &) = delete;
    void start(const std::filesystem::path &video, uint8_t mode);
    void audio(const Audio &audio);
    void clock(const Clock &clock);
    void pause(uint64_t retain_until);
    void resume(uint64_t now);
    void expire(uint64_t now);
    void set_mode(uint8_t mode);
    void abandon() noexcept;
    void stop();
    const std::filesystem::path &directory() const;
    bool active() const;
private:
    struct State;
    std::unique_ptr<State> state_;
};
class StemWriter {
public:
    struct Status {
        std::function<bool()> connected;
        std::function<uint8_t()> state;
        std::function<bool()> refused;
        std::function<void(uint8_t, std::string)> set;
        std::function<void(const std::filesystem::path &)> locate;
    };
    StemWriter(HANDLE shutdown, Status status);
    void expire(uint64_t now);
    void handle(const Event &event, uint64_t now);
    void fail(const char *message);
    void abandon() noexcept;
private:
    static constexpr size_t startup_limit = 2048;
    Status status_;
    Recorder recorder_;
    CaptureDrain drain_;
    std::deque<Event> startup_;
    bool paused_ = false;
    bool failed_ = false;
    bool awaiting_start_ = false;
    bool draining_ = false;
    bool bridge_connected_ = false;
    uint64_t next_expire_ = 0;
    uint64_t final_absolute_ = 0;
    uint64_t watermark_ = 0;
    uint8_t latched_mode_ = 0;
    void finish();
};
}
