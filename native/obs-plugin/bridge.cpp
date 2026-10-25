#include "bridge.hpp"
#include <sddl.h>
#include <util/platform.h>
#include <algorithm>
#include <chrono>
#include <cstring>
#include <stdexcept>

namespace pcobs {
namespace {
struct Handle {
    HANDLE value = INVALID_HANDLE_VALUE;
    explicit Handle(HANDLE handle = INVALID_HANDLE_VALUE) : value(handle) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle &) = delete;
    Handle &operator=(const Handle &) = delete;
};
std::vector<uint8_t> user_token(HANDLE process)
{
    Handle token;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token.value)) throw std::runtime_error("Cannot authenticate the Perfect Comms bridge.");
    DWORD bytes = 0;
    GetTokenInformation(token.value, TokenUser, nullptr, 0, &bytes);
    if (!bytes || bytes > 65536) throw std::runtime_error("Invalid Windows user token.");
    std::vector<uint8_t> user(bytes);
    if (!GetTokenInformation(token.value, TokenUser, user.data(), bytes, &bytes)) throw std::runtime_error("Cannot read Windows user token.");
    return user;
}
bool same_user(HANDLE pipe, const std::vector<uint8_t> &user)
{
    ULONG process_id = 0;
    if (!GetNamedPipeServerProcessId(pipe, &process_id)) return false;
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id));
    if (process.value == nullptr) return false;
    const auto server = user_token(process.value);
    return EqualSid(reinterpret_cast<const TOKEN_USER *>(user.data())->User.Sid,
                    reinterpret_cast<const TOKEN_USER *>(server.data())->User.Sid) != FALSE;
}
bool transfer(HANDLE pipe, HANDLE shutdown, uint8_t *bytes, DWORD count, bool writing)
{
    Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!event.value) return false;
    while (count) {
        OVERLAPPED operation{};
        operation.hEvent = event.value;
        ResetEvent(event.value);
        DWORD transferred = 0;
        BOOL success = writing ? WriteFile(pipe, bytes, count, &transferred, &operation) : ReadFile(pipe, bytes, count, &transferred, &operation);
        if (!success) {
            if (GetLastError() != ERROR_IO_PENDING) return false;
            const HANDLE waits[]{shutdown, event.value};
            const auto waited = WaitForMultipleObjects(2, waits, FALSE, writing ? 1000 : 250);
            if (waited == WAIT_TIMEOUT && !writing) {
                // Wake periodically so status transitions are not blocked by an idle helper.
                CancelIoEx(pipe, &operation);
                GetOverlappedResult(pipe, &operation, &transferred, TRUE);
                if (!transferred) return false;
            } else if (waited != WAIT_OBJECT_0 + 1) {
                CancelIoEx(pipe, &operation);
                GetOverlappedResult(pipe, &operation, &transferred, TRUE);
                return false;
            } else if (!GetOverlappedResult(pipe, &operation, &transferred, FALSE)) return false;
        }
        if (!transferred) return false;
        bytes += transferred;
        count -= transferred;
    }
    return true;
}
std::string utf8(const std::filesystem::path &path)
{
    const auto value = path.u8string();
    return std::string(reinterpret_cast<const char *>(value.data()), value.size());
}
}
struct Bridge::Queue {
    static constexpr size_t capacity = 2048;
    struct Slot { std::atomic<size_t> sequence{}; Event event{}; };
    std::unique_ptr<Slot[]> slots = std::make_unique<Slot[]>(capacity);
    std::atomic<size_t> enqueue{0};
    size_t dequeue = 0;
    Queue() { for (size_t i = 0; i < capacity; ++i) slots[i].sequence.store(i); }
    bool push(const Event &event)
    {
        auto position = enqueue.load(std::memory_order_relaxed);
        for (unsigned attempt = 0; attempt < 32; ++attempt) {
            auto &slot = slots[position % capacity];
            const auto sequence = slot.sequence.load(std::memory_order_acquire);
            const auto difference = intptr_t(sequence) - intptr_t(position);
            if (difference < 0) return false;
            if (difference == 0 && enqueue.compare_exchange_weak(position, position + 1, std::memory_order_relaxed)) {
                slot.event = event;
                slot.sequence.store(position + 1, std::memory_order_release);
                return true;
            }
            position = enqueue.load(std::memory_order_relaxed);
        }
        return false;
    }
    bool pop(Event &event)
    {
        auto &slot = slots[dequeue % capacity];
        if (slot.sequence.load(std::memory_order_acquire) != dequeue + 1) return false;
        event = slot.event;
        slot.sequence.store(dequeue + capacity, std::memory_order_release);
        ++dequeue;
        return true;
    }
};
Bridge::Bridge() : queue_(std::make_unique<Queue>())
{
    shutdown_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!shutdown_) throw std::runtime_error("Cannot create bridge shutdown event.");
    try {
        writer_ = std::thread([this] { write_stems(); });
        reader_ = std::thread([this] { read_pipe(); });
    } catch (...) {
        stopping_.store(true);
        SetEvent(shutdown_);
        if (writer_.joinable()) writer_.join();
        CloseHandle(shutdown_);
        throw;
    }
}
Bridge::~Bridge()
{
    stopping_.store(true);
    SetEvent(shutdown_);
    if (reader_.joinable()) reader_.join();
    if (writer_.joinable()) {
        CancelSynchronousIo(writer_.native_handle());
        writer_.join();
    }
    for (auto *source : sources_) obs_weak_source_release(source);
    CloseHandle(shutdown_);
}
bool Bridge::post(const Event &event)
{
    if (queue_->push(event)) return true;
    overflow_.store(true);
    return false;
}
void Bridge::set_status(uint8_t state, std::string text)
{
    std::lock_guard lock(status_mutex_);
    if (status_state_ == state && status_ == text) return;
    status_state_ = state;
    status_ = std::move(text);
    ++status_revision_;
}
void Bridge::fail(const char *message) { set_status(3, message); }
void Bridge::stop_stems(const char *message)
{
    Event event;
    event.kind = EventKind::failure;
    const auto length = std::min(std::strlen(message), event.message.size() - 1);
    std::memcpy(event.message.data(), message, length);
    post(event);
}
std::string Bridge::status() const { std::lock_guard lock(status_mutex_); return status_; }
std::string Bridge::location() const { std::lock_guard lock(status_mutex_); return location_; }
void Bridge::add_source(obs_source_t *source)
{
    std::lock_guard lock(sources_mutex_);
    auto *weak = obs_source_get_weak_source(source);
    try { sources_.push_back(weak); }
    catch (...) { obs_weak_source_release(weak); throw; }
}
void Bridge::remove_source(obs_source_t *source)
{
    std::lock_guard lock(sources_mutex_);
    std::erase_if(sources_, [source](obs_weak_source_t *weak) {
        if (!obs_weak_source_references_source(weak, source)) return false;
        obs_weak_source_release(weak);
        return true;
    });
}
bool Bridge::source_ready()
{
    std::lock_guard lock(sources_mutex_);
    if (sources_.empty()) return false;
    auto *source = obs_weak_source_get_source(sources_.front());
    if (!source) return false;
    const auto offset = obs_source_get_sync_offset(source);
    obs_source_release(source);
    if (offset != 0) { fail("Set Perfect Comms Sync Offset to 0 ms in Advanced Audio Properties. Stems require an unshifted source."); return false; }
    return true;
}
void Bridge::combined(const Audio &audio)
{
    obs_source_t *source = nullptr;
    {
        std::lock_guard lock(sources_mutex_);
        if (!sources_.empty()) source = obs_weak_source_get_source(sources_.front());
    }
    if (!source) return;
    if (obs_source_get_sync_offset(source) != 0) {
        fail("Set Perfect Comms Sync Offset to 0 ms in Advanced Audio Properties. Stems require an unshifted source.");
    }
    obs_source_audio output{};
    output.data[0] = reinterpret_cast<const uint8_t *>(audio.samples.data());
    output.frames = audio.frames;
    output.speakers = SPEAKERS_STEREO;
    output.format = AUDIO_FORMAT_FLOAT;
    output.samples_per_sec = sample_rate;
    output.timestamp = audio.timestamp;
    obs_source_output_audio(source, &output);
    obs_source_release(source);
}
void Bridge::read_pipe()
{
    try {
        const auto user = user_token(GetCurrentProcess());
        LPWSTR sid = nullptr;
        if (!ConvertSidToStringSidW(reinterpret_cast<const TOKEN_USER *>(user.data())->User.Sid, &sid))
            throw std::runtime_error("Cannot determine Windows bridge user.");
        const auto pipe_name = std::wstring(L"\\\\.\\pipe\\PerfectComms.OBS.") + sid;
        LocalFree(sid);
        uint64_t connection = 0;
        std::vector<uint8_t> bytes(max_message);
        while (!stopping_.load()) {
            Handle pipe(CreateFileW(pipe_name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                                    FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr));
            if (pipe.value == INVALID_HANDLE_VALUE) { WaitForSingleObject(shutdown_, 250); continue; }
            if (!same_user(pipe.value, user)) {
                fail("Perfect Comms bridge Windows user authentication failed.");
                WaitForSingleObject(shutdown_, 1000);
                continue;
            }
            ++connection;
            bool greeted = false;
            uint64_t sent_revision = 0;
            uint64_t combined_timestamp = 0;
            while (!stopping_.load()) {
                uint8_t state;
                uint64_t revision;
                std::string text;
                {
                    std::lock_guard lock(status_mutex_);
                    state = status_state_;
                    revision = status_revision_;
                    if (revision != sent_revision) text = status_;
                }
                if (revision != sent_revision) {
                    text.resize(std::min<size_t>(text.size(), 1024));
                    const uint32_t length = uint32_t(4 + text.size());
                    std::array<uint8_t, 1032> reply{};
                    std::memcpy(reply.data(), &length, 4);
                    reply[4] = 3;
                    reply[5] = state;
                    reply[6] = uint8_t(text.size());
                    reply[7] = uint8_t(text.size() >> 8);
                    std::memcpy(reply.data() + 8, text.data(), text.size());
                    if (!transfer(pipe.value, shutdown_, reply.data(), length + 4, true)) break;
                    sent_revision = revision;
                }
                DWORD available = 0;
                if (!PeekNamedPipe(pipe.value, nullptr, 0, nullptr, &available, nullptr)) break;
                if (!available) { WaitForSingleObject(shutdown_, 10); continue; }
                uint32_t length = 0;
                if (!transfer(pipe.value, shutdown_, reinterpret_cast<uint8_t *>(&length), 4, false)) break;
                if (!length || length > max_message) { fail("Perfect Comms bridge sent an invalid message length."); break; }
                if (!transfer(pipe.value, shutdown_, bytes.data(), length, false)) break;
                Hello hello;
                Audio audio;
                if (!parse_message(std::span(bytes.data(), length), hello, audio) || (!greeted && bytes[0] != 1)) {
                    fail("Perfect Comms bridge sent malformed audio or protocol data.");
                    break;
                }
                Event event;
                if (bytes[0] == 1) {
                    mode_.store(hello.mode);
                    connected_.store(true);
                    greeted = true;
                    event.kind = EventKind::hello;
                    if (!post(event)) break;
                } else {
                    const auto now = os_gettime_ns();
                    if (audio.timestamp > now + 1000000000ULL || now > audio.timestamp + 10000000000ULL ||
                        (!audio.track && audio.timestamp < combined_timestamp)) {
                        fail("Perfect Comms bridge audio timestamp is invalid.");
                        break;
                    }
                    audio.connection = connection;
                    if (!audio.track) {
                        combined_timestamp = audio.timestamp;
                        combined(audio);
                        event.kind = EventKind::watermark;
                        event.clock.absolute = audio.timestamp;
                        if (!post(event)) break;
                    }
                    else { event.kind = EventKind::audio; event.audio = audio; if (!post(event)) break; }
                }
            }
            if (!stopping_.load()) {
                std::string text;
                uint8_t state;
                { std::lock_guard lock(status_mutex_); text = status_; state = status_state_; }
                if (state == 3) {
                    text.resize(std::min<size_t>(text.size(), 1024));
                    const uint32_t length = uint32_t(4 + text.size());
                    std::array<uint8_t, 1032> reply{};
                    std::memcpy(reply.data(), &length, 4);
                    reply[4] = 3;
                    reply[5] = 3;
                    reply[6] = uint8_t(text.size());
                    reply[7] = uint8_t(text.size() >> 8);
                    std::memcpy(reply.data() + 8, text.data(), text.size());
                    transfer(pipe.value, shutdown_, reply.data(), length + 4, true);
                }
            }
            connected_.store(false);
            Event event;
            event.kind = EventKind::disconnected;
            post(event);
            WaitForSingleObject(shutdown_, 250);
        }
    } catch (const std::exception &error) {
        connected_.store(false);
        fail(error.what());
        Event event;
        event.kind = EventKind::disconnected;
        post(event);
    }
}
void Bridge::write_stems()
{
    try {
        Recorder recorder(shutdown_);
        bool paused = false;
        bool failed = false;
        bool awaiting_start = false;
        bool draining = false;
        bool bridge_connected = false;
        uint64_t next_expire = 0;
        uint64_t final_absolute = 0;
        uint64_t watermark = 0;
        uint8_t latched_mode = 0;
        CaptureDrain drain;
        std::vector<Event> startup;
        startup.reserve(2048);
        const auto finish = [&] {
            draining = false;
            recorder.stop();
            bool existing_error;
            { std::lock_guard lock(status_mutex_); existing_error = status_state_ == 3; }
            if (!failed && !existing_error) set_status(0, connected() ? "Ready. OBS recording controls voice stems." : "Waiting for Perfect Comms.");
        };
        while (!stopping_.load()) {
            try {
                if (overflow_.exchange(false)) throw std::runtime_error("Voice stem writer queue overflowed. Recording stems stopped.");
                const auto now = os_gettime_ns();
                if (draining) drain.expire(now);
                else if (now >= next_expire) {
                    recorder.expire(now);
                    next_expire = now + 20000000;
                }
                Event event;
                if (!queue_->pop(event)) {
                    WaitForSingleObject(shutdown_, 5);
                    continue;
                }
                if (awaiting_start && (event.kind == EventKind::audio || event.kind == EventKind::clock)) {
                    if (startup.size() >= 2048) throw std::runtime_error("Voice stem startup queue overflowed.");
                    startup.push_back(event);
                    continue;
                }
                switch (event.kind) {
                case EventKind::arm:
                    if (draining) throw std::runtime_error("OBS started another recording before previous voice audio drained.");
                    recorder.stop();
                    awaiting_start = true;
                    latched_mode = event.mode;
                    final_absolute = 0;
                    failed = false;
                    startup.clear();
                    break;
                case EventKind::start:
                    if (failed) break;
                    paused = false;
                    recorder.start(std::filesystem::path(event.path.data()), awaiting_start ? latched_mode : event.mode);
                    awaiting_start = false;
                    for (const auto &buffered : startup) {
                        if (buffered.kind == EventKind::audio) recorder.audio(buffered.audio);
                        else {
                            recorder.clock(buffered.clock);
                            final_absolute = std::max(final_absolute, buffered.clock.absolute + buffered.clock.duration);
                        }
                    }
                    startup.clear();
                    { std::lock_guard lock(status_mutex_); location_ = utf8(recorder.directory()); }
                    set_status(connected() ? 1 : 3, connected() ? "Recording synchronized voice stems." :
                        "OBS started without the mod bridge. Enable OBS recording in Perfect Comms; late joins will be aligned.");
                    break;
                case EventKind::audio: if (!failed) recorder.audio(event.audio); break;
                case EventKind::clock:
                    if (!failed) {
                        recorder.clock(event.clock);
                        final_absolute = std::max(final_absolute, event.clock.absolute + event.clock.duration);
                    }
                    break;
                case EventKind::stop:
                    awaiting_start = false;
                    startup.clear();
                    if (failed || !recorder.active() || (!bridge_connected && !connected())) { finish(); break; }
                    drain.start(final_absolute, os_gettime_ns());
                    draining = true;
                    if (drain.ready(watermark)) finish();
                    break;
                case EventKind::watermark:
                    watermark = event.clock.absolute;
                    if (draining && drain.ready(watermark)) finish();
                    break;
                case EventKind::pause:
                    paused = true;
                    recorder.pause(event.clock.absolute + event.clock.duration);
                    if (!failed) set_status(2, "Voice stems paused with OBS.");
                    break;
                case EventKind::resume:
                    paused = false;
                    recorder.resume(event.clock.absolute);
                    if (!failed) set_status(1, "Recording synchronized voice stems.");
                    break;
                case EventKind::hello:
                    bridge_connected = true;
                    if (!failed) set_status(recorder.active() ? (paused ? 2 : 1) : 0,
                        recorder.active() ? "Connected. Recording aligned voice stems." : "Ready. OBS recording controls voice stems.");
                    break;
                case EventKind::disconnected: {
                    bridge_connected = false;
                    if (draining) throw std::runtime_error("Perfect Comms disconnected before final voice audio drained. Voice stems may be incomplete.");
                    bool existing_error;
                    { std::lock_guard lock(status_mutex_); existing_error = status_state_ == 3; }
                    if (!failed && !existing_error) set_status(recorder.active() ? 3 : 0,
                        recorder.active() ? "Mod bridge disconnected. Missing voice is silence; reconnect starts new aligned stems." : "Waiting for Perfect Comms.");
                    break;
                }
                case EventKind::failure: throw std::runtime_error(event.message.data());
                }
            } catch (const std::exception &error) {
                failed = true;
                awaiting_start = false;
                draining = false;
                startup.clear();
                if (stopping_.load()) { recorder.abandon(); break; }
                fail(error.what());
                try { recorder.stop(); } catch (const std::exception &finalize_error) { fail(finalize_error.what()); }
            }
        }
        recorder.abandon();
    } catch (const std::exception &error) {
        try { fail(error.what()); } catch (...) { blog(LOG_ERROR, "[Perfect Comms] Voice stem writer initialization failed."); }
    } catch (...) {
        try { fail("Voice stem writer failed unexpectedly."); } catch (...) { blog(LOG_ERROR, "[Perfect Comms] Voice stem writer failed."); }
    }
}
}
