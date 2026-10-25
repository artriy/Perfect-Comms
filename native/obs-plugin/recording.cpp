#include "recording.hpp"
#include <windows.h>
#include <winioctl.h>
#include <winternl.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <deque>
#include <limits>
#include <map>
#include <stdexcept>
#include <tuple>

namespace pcobs {
namespace {
uint64_t integer(std::span<const uint8_t> bytes, size_t offset, size_t count)
{
    uint64_t value = 0;
    for (size_t i = 0; i < count; ++i) value |= uint64_t(bytes[offset + i]) << (i * 8);
    return value;
}
void put(std::span<uint8_t> bytes, size_t offset, uint64_t value, size_t count)
{
    for (size_t i = 0; i < count; ++i) bytes[offset + i] = uint8_t(value >> (i * 8));
}
std::wstring wide(std::string_view text)
{
    if (text.empty()) return {};
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), int(text.size()), nullptr, 0);
    if (!count) return {};
    std::wstring value(size_t(count), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), int(text.size()), value.data(), count);
    return value;
}
void disk_error() { throw std::runtime_error("Voice stem disk operation failed. Check free space and folder permissions."); }
struct Wave {
    HANDLE file = INVALID_HANDLE_VALUE;
    HANDLE completed = nullptr;
    HANDLE shutdown = nullptr;
    uint64_t length = 0;
    Wave(HANDLE directory, std::wstring_view filename, HANDLE cancellation) : shutdown(cancellation)
    {
        check_cancelled();
        completed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!completed) disk_error();
        try {
            UNICODE_STRING name{};
            name.Length = USHORT(filename.size() * sizeof(wchar_t));
            name.MaximumLength = name.Length;
            name.Buffer = const_cast<wchar_t *>(filename.data());
            OBJECT_ATTRIBUTES attributes{};
            InitializeObjectAttributes(&attributes, &name, OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE, directory, nullptr);
            IO_STATUS_BLOCK status{};
            if (NtCreateFile(&file, GENERIC_READ | GENERIC_WRITE | SYNCHRONIZE, &attributes, &status, nullptr,
                FILE_ATTRIBUTE_NORMAL, FILE_SHARE_READ, FILE_CREATE, FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
                nullptr, 0) < 0) disk_error();
            check_cancelled();
            OVERLAPPED operation{};
            operation.hEvent = completed;
            DWORD transferred = 0;
            const auto success = DeviceIoControl(file, FSCTL_SET_SPARSE, nullptr, 0, nullptr, 0, &transferred, &operation);
            if (!success && (GetLastError() == ERROR_INVALID_FUNCTION || GetLastError() == ERROR_NOT_SUPPORTED))
                throw std::runtime_error("Voice stems require a volume supporting sparse files, such as NTFS or ReFS.");
            finish_io(success, operation, transferred);
            header();
        } catch (...) {
            if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
            CloseHandle(completed);
            throw;
        }
    }
    ~Wave() { if (file != INVALID_HANDLE_VALUE) CloseHandle(file); if (completed) CloseHandle(completed); }
    void check_cancelled() const
    {
        if (shutdown && WaitForSingleObject(shutdown, 0) == WAIT_OBJECT_0)
            throw std::runtime_error("Voice stem file operation cancelled during shutdown.");
    }
    void finish_io(BOOL success, OVERLAPPED &operation, DWORD &transferred)
    {
        if (!success) {
            if (GetLastError() != ERROR_IO_PENDING) disk_error();
            const HANDLE waits[]{completed, shutdown};
            const auto waited = WaitForMultipleObjects(shutdown ? 2 : 1, waits, FALSE, INFINITE);
            if (waited != WAIT_OBJECT_0) {
                CancelIoEx(file, &operation);
                GetOverlappedResult(file, &operation, &transferred, TRUE);
                check_cancelled();
                disk_error();
            }
            if (!GetOverlappedResult(file, &operation, &transferred, FALSE)) disk_error();
        }
    }
    void transfer(void *bytes, DWORD count, uint64_t offset, bool writing)
    {
        check_cancelled();
        OVERLAPPED operation{};
        operation.hEvent = completed;
        operation.Offset = DWORD(offset);
        operation.OffsetHigh = DWORD(offset >> 32);
        ResetEvent(completed);
        DWORD transferred = 0;
        const auto success = writing ? WriteFile(file, bytes, count, &transferred, &operation) :
                                       ReadFile(file, bytes, count, &transferred, &operation);
        finish_io(success, operation, transferred);
        if (transferred != count) disk_error();
    }
    void header()
    {
        auto bytes = wave_header(length);
        transfer(bytes.data(), DWORD(bytes.size()), 0, true);
    }
    void resize(uint64_t frames)
    {
        check_cancelled();
        if (frames > (uint64_t(INT64_MAX) - 80) / 8) disk_error();
        LARGE_INTEGER position;
        position.QuadPart = LONGLONG(80 + frames * 8);
        if (!SetFilePointerEx(file, position, nullptr, FILE_BEGIN) || !SetEndOfFile(file)) disk_error();
        length = frames;
    }
    void add(uint64_t start, std::span<const float> samples)
    {
        const auto frames = samples.size() / 2;
        if (start > UINT64_MAX - frames) disk_error();
        if (start + frames > length) resize(start + frames);
        std::array<float, max_frames * 2> mixed{};
        const auto size = DWORD(samples.size_bytes());
        transfer(mixed.data(), size, 80 + start * 8, false);
        for (size_t i = 0; i < samples.size(); ++i) {
            mixed[i] += samples[i];
            if (!std::isfinite(mixed[i])) throw std::runtime_error("Voice stem audio exceeded floating point range.");
        }
        transfer(mixed.data(), size, 80 + start * 8, true);
        header();
    }
    void finish(uint64_t frames)
    {
        resize(frames);
        header();
        check_cancelled();
        if (!FlushFileBuffers(file)) disk_error();
    }
};}

uint64_t frames_at(uint64_t ns)
{
    return ns / 1000000000 * sample_rate + (ns % 1000000000 * sample_rate + 500000000) / 1000000000;
}

bool parse_message(std::span<const uint8_t> bytes, Hello &hello, Audio &audio)
{
    if (bytes.empty() || bytes.size() > max_message) return false;
    if (bytes[0] == 1) {
        if (bytes.size() != 12 || integer(bytes, 1, 4) != 1 || bytes[5] > 1 ||
            integer(bytes, 6, 4) != sample_rate || integer(bytes, 10, 2) != 2) return false;
        hello.mode = bytes[5];
        return true;
    }
    if (bytes[0] != 2 || bytes.size() < 18) return false;
    const auto name_length = size_t(integer(bytes, 14, 2));
    if (name_length > 192 || bytes.size() < 18 + name_length || bytes[13] > 2) return false;
    const auto frames = uint16_t(integer(bytes, 16 + name_length, 2));
    if (frames == 0 || frames > max_frames || bytes.size() != 18 + name_length + size_t(frames) * 8) return false;
    const auto name = std::string_view(reinterpret_cast<const char *>(bytes.data() + 16), name_length);
    if (name.find('\0') != std::string_view::npos || (name_length && wide(name).empty())) return false;
    Audio parsed;
    parsed.timestamp = integer(bytes, 1, 8);
    parsed.track = uint32_t(integer(bytes, 9, 4));
    parsed.group = bytes[13];
    parsed.frames = frames;
    if (!parsed.timestamp || (!parsed.track && name_length)) return false;
    std::memcpy(parsed.name.data(), name.data(), name_length);
    std::memcpy(parsed.samples.data(), bytes.data() + 18 + name_length, size_t(frames) * 8);
    for (size_t i = 0; i < size_t(frames) * 2; ++i) if (!std::isfinite(parsed.samples[i])) return false;
    audio = parsed;
    return true;
}

std::wstring safe_name(std::string_view name)
{
    auto value = wide(name);
    if (value.empty()) return L"Voice";
    if (value.size() > 80) {
        value.resize(80);
        if (value.back() >= 0xd800 && value.back() <= 0xdbff) value.pop_back();
    }
    for (auto &ch : value) {
        if (ch < 32 || (ch >= 127 && ch <= 159) || std::wstring_view(L"<>:\"/\\|?*").find(ch) != std::wstring_view::npos)
            ch = L'_';
    }
    for (size_t i = 0; i < value.size() && (value[i] == L'.' || value[i] == L' '); ++i) value[i] = L'_';
    while (!value.empty() && (value.back() == L'.' || value.back() == L' ')) value.pop_back();
    if (value.empty()) value = L"Voice";
    auto base = value.substr(0, value.find(L'.'));
    for (auto &ch : base) if (ch >= L'a' && ch <= L'z') ch -= L'a' - L'A';
    if (base == L"CON" || base == L"PRN" || base == L"AUX" || base == L"NUL" || base == L"CONIN$" || base == L"CONOUT$" ||
        (base.size() == 4 && (base.starts_with(L"COM") || base.starts_with(L"LPT")) &&
         (base[3] >= L'1' && base[3] <= L'9' || base[3] == L'\u00b9' || base[3] == L'\u00b2' || base[3] == L'\u00b3')))
        value.insert(value.begin(), L'_');
    return value;
}

std::array<uint8_t, 80> wave_header(uint64_t frames)
{
    std::array<uint8_t, 80> bytes{};
    const uint64_t size = frames * 8;
    const bool rf64 = size > UINT32_MAX - 72;
    std::memcpy(bytes.data(), rf64 ? "RF64" : "RIFF", 4);
    put(bytes, 4, rf64 ? UINT32_MAX : size + 72, 4);
    std::memcpy(bytes.data() + 8, "WAVE", 4);
    std::memcpy(bytes.data() + 12, rf64 ? "ds64" : "JUNK", 4);
    put(bytes, 16, 28, 4);
    if (rf64) { put(bytes, 20, size + 72, 8); put(bytes, 28, size, 8); put(bytes, 36, frames, 8); }
    std::memcpy(bytes.data() + 48, "fmt ", 4);
    put(bytes, 52, 16, 4);
    put(bytes, 56, 3, 2);
    put(bytes, 58, 2, 2);
    put(bytes, 60, sample_rate, 4);
    put(bytes, 64, sample_rate * 8, 4);
    put(bytes, 68, 8, 2);
    put(bytes, 70, 32, 2);
    std::memcpy(bytes.data() + 72, "data", 4);
    put(bytes, 76, rf64 ? UINT32_MAX : size, 4);
    return bytes;
}

struct Recorder::State {
    uint64_t retained_from = 0;
    uint64_t latest_clock_end = 0;
    struct Pending { Audio audio; std::bitset<max_frames> written; };
    std::filesystem::path directory;
    HANDLE directory_handle = INVALID_HANDLE_VALUE;
    ~State() { if (directory_handle != INVALID_HANDLE_VALUE) CloseHandle(directory_handle); }
    bool active = false;
    uint8_t mode = 0;
    uint64_t duration = 0;
    HANDLE shutdown = nullptr;
    bool paused = false;
    uint64_t pause_retain_until = 0;
    uint64_t resume_guard_until = 0;
    struct MappedClock { Clock timing; int64_t sample_offset; };
    std::vector<MappedClock> clocks;
    std::vector<uint64_t> resume_boundaries;
    std::deque<Pending> pending;
    std::map<std::pair<uint64_t, uint32_t>, uint64_t> chronology;
    std::map<std::tuple<uint64_t, uint32_t, uint8_t>, std::unique_ptr<Wave>> waves;
    auto resume_after(uint64_t absolute)
    {
        return std::upper_bound(resume_boundaries.begin(), resume_boundaries.end(), absolute);
    }
    bool continuous(const Clock &before, const Clock &after)
    {
        if (after.absolute < before.absolute || after.relative < before.relative ||
            frames_at(after.absolute - before.absolute) != frames_at(after.relative - before.relative)) return false;
        const auto resume = resume_after(before.absolute);
        return resume == resume_boundaries.end() || *resume > after.absolute;
    }
    Wave &wave(const Audio &audio)
    {
        const uint8_t group = audio.group == 0 ? 0 : mode ? audio.group : 3;
        const auto key = std::tuple(group == 3 ? audio.connection : 0, group == 3 ? audio.track : 0, group);
        auto found = waves.find(key);
        if (found != waves.end()) return *found->second;
        if (waves.size() >= max_tracks) throw std::runtime_error("Voice stem track limit (256) reached.");
        auto name = group == 0 ? L"Private voices" : group == 1 ? L"Alive voices" : group == 2 ? L"Dead voices" :
                    std::to_wstring(audio.connection) + L"-" + std::to_wstring(audio.track) + L"-" + safe_name(audio.name.data());
        return *waves.emplace(key, std::make_unique<Wave>(directory_handle, name + L".wav", shutdown)).first->second;
    }
    void apply_gap(Pending &pending_audio, const MappedClock &before, const MappedClock &after)
    {
        const auto end = before.timing.absolute + before.timing.duration;
        if (after.timing.absolute <= end || before.sample_offset != after.sample_offset ||
            !continuous(before.timing, after.timing)) return;
        apply(pending_audio, {{end, before.timing.relative + before.timing.duration, after.timing.absolute - end}, before.sample_offset});
    }
    void apply(Pending &pending_audio, const MappedClock &mapped)
    {
        const auto &clock = mapped.timing;
        const auto first = frames_at(pending_audio.audio.timestamp);
        const auto begin = frames_at(clock.absolute);
        const auto end = frames_at(clock.absolute + clock.duration);
        const auto offset = mapped.sample_offset;
        if (end <= first || first + pending_audio.audio.frames <= begin) return;
        size_t index = size_t(begin > first ? begin - first : 0);
        const auto limit = size_t(std::min<uint64_t>(pending_audio.audio.frames, end - first));
        while (index < limit) {
            if (pending_audio.written[index]) { ++index; continue; }
            const auto start = index;
            while (index < limit && !pending_audio.written[index]) {
                pending_audio.written.set(index++);
            }
            const auto destination = int64_t(first + start) + offset;
            if (destination < 0) throw std::runtime_error("OBS audio clock maps before the recording timeline.");
            wave(pending_audio.audio).add(uint64_t(destination),
                std::span(pending_audio.audio.samples.data() + start * 2, (index - start) * 2));
        }
    }
};
Recorder::Recorder(HANDLE shutdown) : state_(std::make_unique<State>()) { state_->shutdown = shutdown; }
Recorder::~Recorder() = default;
const std::filesystem::path &Recorder::directory() const { return state_->directory; }
bool Recorder::active() const { return state_->active; }
void Recorder::start(const std::filesystem::path &video, uint8_t mode)
{
    stop();
    if (mode > 1 || video.empty() || !video.has_filename() || !video.has_parent_path())
        throw std::runtime_error("OBS recording path is unavailable. Choose a local recording file.");
    auto next = std::make_unique<State>();
    next->mode = mode;
    next->shutdown = state_->shutdown;
    const auto base = video.parent_path() / (video.stem().wstring() + L"-voices");
    for (unsigned suffix = 0; suffix < 10000; ++suffix) {
        auto candidate = suffix ? std::filesystem::path(base.wstring() + L"-" + std::to_wstring(suffix)) : base;
        auto native = std::filesystem::absolute(candidate).lexically_normal().wstring();
        if (native.starts_with(LR"(\\?\)")) native.replace(0, 4, LR"(\??\)");
        else if (native.starts_with(LR"(\\)")) native = LR"(\??\UNC\)" + native.substr(2);
        else native.insert(0, LR"(\??\)");
        if (native.size() > std::numeric_limits<USHORT>::max() / sizeof(wchar_t)) disk_error();
        UNICODE_STRING name{};
        name.Length = USHORT(native.size() * sizeof(wchar_t));
        name.MaximumLength = name.Length;
        name.Buffer = native.data();
        OBJECT_ATTRIBUTES attributes{};
        InitializeObjectAttributes(&attributes, &name, OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE, nullptr, nullptr);
        IO_STATUS_BLOCK status{};
        const auto created = NtCreateFile(&next->directory_handle,
            FILE_LIST_DIRECTORY | FILE_TRAVERSE | FILE_READ_ATTRIBUTES | SYNCHRONIZE, &attributes, &status, nullptr,
            FILE_ATTRIBUTE_DIRECTORY, FILE_SHARE_READ, FILE_CREATE, FILE_DIRECTORY_FILE | FILE_SYNCHRONOUS_IO_NONALERT,
            nullptr, 0);
        if (created >= 0) {
            next->directory = std::move(candidate);
            next->active = true;
            state_ = std::move(next);
            return;
        }
        if (RtlNtStatusToDosError(created) != ERROR_ALREADY_EXISTS) disk_error();
    }
    throw std::runtime_error("No unused voice stem folder name is available.");
}
void Recorder::audio(const Audio &audio)
{
    if (!state_->active || !audio.track) return;
    if (!audio.frames || audio.frames > max_frames || audio.group > 2) throw std::runtime_error("Invalid voice stem frame.");
    if (state_->paused && audio.timestamp >= state_->pause_retain_until) return;
    if (audio.timestamp + 20000000 < state_->retained_from)
        throw std::runtime_error("Voice stem arrived after the retained output clock window.");
    const auto key = std::pair(audio.connection, audio.track);
    const auto found = state_->chronology.find(key);
    if (found != state_->chronology.end() && audio.timestamp < found->second)
        throw std::runtime_error("Voice stem timestamps moved backwards.");
    if (found == state_->chronology.end() && state_->chronology.size() >= max_tracks * 4)
        throw std::runtime_error("Voice stem identity limit reached.");
    state_->chronology[key] = audio.timestamp;
    State::Pending pending{audio, {}};
    for (size_t index = 0; index < state_->clocks.size(); ++index) {
        state_->apply(pending, state_->clocks[index]);
        if (index) state_->apply_gap(pending, state_->clocks[index - 1], state_->clocks[index]);
    }
    if (pending.written.count() == audio.frames) return;
    if (state_->pending.size() >= 8192) throw std::runtime_error("Voice stem timing queue overflowed. Recording stems stopped.");
    state_->pending.push_back(std::move(pending));
}
void Recorder::clock(const Clock &clock)
{
    if (!state_->active) return;
    if (!clock.duration || clock.duration > 1000000000 || clock.absolute > UINT64_MAX - clock.duration ||
        clock.relative > UINT64_MAX - clock.duration || clock.absolute + clock.duration < state_->retained_from)
        throw std::runtime_error("OBS output clock exceeded retained audio timing. Recording stems stopped.");
    if (state_->clocks.size() >= 16384) throw std::runtime_error("OBS output clock queue overflowed.");
    std::optional<int64_t> overlapping_offset;
    for (const auto &mapped : state_->clocks) {
        const auto &previous = mapped.timing;
        const auto begin = std::max(clock.absolute, previous.absolute);
        const auto end = std::min(clock.absolute + clock.duration, previous.absolute + previous.duration);
        if (clock.relative == previous.relative)
            throw std::runtime_error("OBS output clock contains overlapping frames.");
        if (end > begin && end - begin > 1000) {
            const auto &before = clock.absolute < previous.absolute ? clock : previous;
            const auto &after = clock.absolute < previous.absolute ? previous : clock;
            const auto resume = state_->resume_after(before.absolute);
            // Compare elapsed samples, not separately rounded absolute clock phases.
            if (after.relative < before.relative ||
                frames_at(after.absolute - before.absolute) != frames_at(after.relative - before.relative) ||
                (resume != state_->resume_boundaries.end() && *resume < std::max(clock.absolute + clock.duration, previous.absolute + previous.duration)))
                throw std::runtime_error("OBS output clocks overlap with inconsistent audio timing or across a pause.");
            if (overlapping_offset && *overlapping_offset != mapped.sample_offset)
                throw std::runtime_error("OBS overlapping clocks have inconsistent audio sample phases.");
            overlapping_offset = mapped.sample_offset;
        }
    }
    const auto next = std::lower_bound(state_->clocks.begin(), state_->clocks.end(), clock.absolute,
        [](const State::MappedClock &existing, uint64_t absolute) { return existing.timing.absolute < absolute; });
    if ((next != state_->clocks.begin() && (next - 1)->timing.relative >= clock.relative) ||
        (next != state_->clocks.end() && next->timing.relative <= clock.relative))
        throw std::runtime_error("OBS presentation clocks moved backwards.");
    auto offset = int64_t(frames_at(clock.relative)) - int64_t(frames_at(clock.absolute));
    const bool from_previous = next != state_->clocks.begin() && state_->continuous((next - 1)->timing, clock);
    const bool from_next = next != state_->clocks.end() && state_->continuous(clock, next->timing);
    if (from_previous) offset = (next - 1)->sample_offset;
    if (from_next) {
        if (from_previous && offset != next->sample_offset)
            throw std::runtime_error("OBS clocks join inconsistent audio sample phases.");
        offset = next->sample_offset;
    }
    if (overlapping_offset && *overlapping_offset != offset)
        throw std::runtime_error("OBS audio clock changes phase inside an overlapping sample interval.");
    const auto inserted = state_->clocks.insert(next, {clock, offset});
    state_->duration = std::max(state_->duration, frames_at(clock.relative + clock.duration));
    state_->latest_clock_end = std::max(state_->latest_clock_end, clock.absolute + clock.duration);
    for (auto &pending : state_->pending) {
        state_->apply(pending, *inserted);
        if (inserted != state_->clocks.begin()) state_->apply_gap(pending, *(inserted - 1), *inserted);
        if (inserted + 1 != state_->clocks.end()) state_->apply_gap(pending, *inserted, *(inserted + 1));
    }
    std::erase_if(state_->pending, [](const State::Pending &pending) { return pending.written.count() == pending.audio.frames; });
}
void Recorder::pause(uint64_t retain_until)
{
    state_->paused = true;
    state_->pause_retain_until = retain_until;
}
void Recorder::resume(uint64_t now)
{
    if (state_->paused) {
        if (state_->resume_boundaries.size() >= 16384) throw std::runtime_error("OBS pause epoch queue overflowed.");
        state_->resume_boundaries.push_back(now);
    }
    state_->paused = false;
    state_->resume_guard_until = now > UINT64_MAX - 10000000000ULL ? UINT64_MAX : now + 10000000000ULL;
}
void Recorder::expire(uint64_t now)
{
    if (!state_->active || state_->paused || now < state_->resume_guard_until) return;
    const auto threshold = now > 10000000000ULL ? now - 10000000000ULL : 0;
    while (!state_->pending.empty()) {
        const auto &audio = state_->pending.front().audio;
        if (audio.timestamp + 20000000 >= threshold) break;
        bool mapped = false;
        for (const auto &mapped_clock : state_->clocks) {
            const auto &clock = mapped_clock.timing;
            if (audio.timestamp < clock.absolute + clock.duration && clock.absolute < audio.timestamp + 20000000) { mapped = true; break; }
        }
        if (!mapped && audio.timestamp >= state_->latest_clock_end)
            throw std::runtime_error("OBS stopped delivering the recording clock. Recording stems stopped.");
        state_->pending.pop_front();
    }
    state_->retained_from = threshold;
    auto first_retained = std::find_if(state_->clocks.begin(), state_->clocks.end(),
        [threshold](const State::MappedClock &clock) { return clock.timing.absolute + clock.timing.duration >= threshold; });
    // Keep the preceding clock as the mapping anchor for retained audio inside a video gap.
    if (first_retained != state_->clocks.begin()) --first_retained;
    state_->clocks.erase(state_->clocks.begin(), first_retained);
    const auto earliest_clock = state_->clocks.empty() ? threshold : state_->clocks.front().timing.absolute;
    auto first_resume = state_->resume_after(earliest_clock);
    if (first_resume != state_->resume_boundaries.begin()) --first_resume;
    state_->resume_boundaries.erase(state_->resume_boundaries.begin(), first_resume);
}
void Recorder::abandon() noexcept
{
    state_->active = false;
    state_->waves.clear();
    state_->pending.clear();
    state_->clocks.clear();
    state_->resume_boundaries.clear();
    state_->chronology.clear();
    if (state_->directory_handle != INVALID_HANDLE_VALUE) {
        CloseHandle(state_->directory_handle);
        state_->directory_handle = INVALID_HANDLE_VALUE;
    }
}
void Recorder::stop()
{
    if (!state_->active) return;
    state_->active = false;
    std::exception_ptr failure;
    for (auto &[key, wave] : state_->waves) {
        try { wave->finish(state_->duration); } catch (...) { if (!failure) failure = std::current_exception(); }
    }
    state_->waves.clear();
    state_->pending.clear();
    state_->clocks.clear();
    state_->resume_boundaries.clear();
    state_->chronology.clear();
    if (state_->directory_handle != INVALID_HANDLE_VALUE) {
        CloseHandle(state_->directory_handle);
        state_->directory_handle = INVALID_HANDLE_VALUE;
    }
    if (failure) std::rethrow_exception(failure);
}
}
