#pragma once
#include "recording.hpp"
#include <obs.h>
#include <atomic>
#include <functional>
#include <mutex>
#include <thread>
#include <windows.h>

namespace pcobs {
enum class EventKind { audio, clock, arm, start, stop, pause, resume, hello, disconnected, watermark, failure };
struct Event {
    EventKind kind = EventKind::audio;
    Audio audio{};
    Clock clock{};
    uint8_t mode = 0;
    std::array<char, 256> message{};
    std::array<wchar_t, 1024> path{};
};
class Bridge {
public:
    Bridge();
    ~Bridge();
    bool post(const Event &event);
    void add_source(obs_source_t *source);
    void remove_source(obs_source_t *source);
    std::string status() const;
    std::string location() const;
    bool source_ready();
    bool connected() const { return connected_.load(); }
    uint8_t mode() const { return mode_.load(); }
    void fail(const char *message);
    void stop_stems(const char *message);
private:
    struct Queue;
    std::unique_ptr<Queue> queue_;
    std::thread reader_;
    std::thread writer_;
    HANDLE shutdown_ = nullptr;
    std::atomic<bool> stopping_{false};
    std::atomic<bool> overflow_{false};
    std::atomic<bool> connected_{false};
    std::atomic<uint8_t> mode_{0};
    mutable std::mutex status_mutex_;
    std::string status_ = "Waiting for Perfect Comms. Enable OBS recording in the mod.";
    std::string location_;
    uint8_t status_state_ = 0;
    uint64_t status_revision_ = 1;
    std::mutex sources_mutex_;
    std::vector<obs_weak_source_t *> sources_;
    void set_status(uint8_t state, std::string text);
    void read_pipe();
    void write_stems();
    void combined(const Audio &audio);
};
}
