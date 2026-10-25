#include "recording.hpp"
#include <stdexcept>

namespace pcobs {
namespace {
uint64_t ticks_to_ns(uint64_t ticks, int32_t denominator)
{
    const auto seconds = ticks / uint32_t(denominator);
    if (seconds > (UINT64_MAX - 1000000000ULL) / 1000000000ULL)
        throw std::runtime_error("OBS video timestamp overflowed.");
    return seconds * 1000000000ULL + (ticks % uint32_t(denominator) * 1000000000ULL + denominator / 2) / uint32_t(denominator);
}
}
Clock video_clock(int64_t pts, int32_t numerator, int32_t denominator, uint64_t composition, int64_t presentation_origin)
{
    if (presentation_origin < 0 || pts < presentation_origin || numerator <= 0 || denominator <= 0 || !composition)
        throw std::runtime_error("OBS did not provide a valid composition timestamp. Voice stems stopped rather than guessing synchronization.");
    const auto relative = ticks_to_ns(uint64_t(pts - presentation_origin), denominator);
    const auto duration = ticks_to_ns(uint32_t(numerator), denominator);
    if (!duration || duration > 1000000000ULL || relative > UINT64_MAX - duration || composition > UINT64_MAX - duration)
        throw std::runtime_error("OBS video timing is outside the supported range.");
    return {composition, relative, duration};
}
std::optional<Clock> PacketAcceptance::resolve(uint64_t total_bytes, bool finishing)
{
    std::optional<Clock> committed;
    if (pending_) {
        pending_ = false;
        if (total_bytes == before_ + size_) {
            if (clock_) {
                if (buffered_video_) {
                    if (buffered_clock_) {
                        if (decode_->denominator != buffered_decode_->denominator || decode_->ticks <= buffered_decode_->ticks)
                            throw std::runtime_error("OBS hybrid video decode timestamps are inconsistent. Voice stems stopped.");
                        const auto ticks = uint64_t(decode_->ticks) - uint64_t(buffered_decode_->ticks);
                        if (ticks > uint32_t(decode_->denominator))
                            throw std::runtime_error("OBS hybrid video sample duration exceeds the supported range. Voice stems stopped.");
                        committed = buffered_clock_;
                        committed->duration = ticks_to_ns(ticks, decode_->denominator);
                        if (!committed->duration || committed->absolute > UINT64_MAX - committed->duration ||
                            committed->relative > UINT64_MAX - committed->duration)
                            throw std::runtime_error("OBS hybrid video sample duration is invalid. Voice stems stopped.");
                    }
                    buffered_clock_ = clock_;
                    buffered_decode_ = decode_;
                } else {
                    committed = clock_;
                }
            }
        } else if (!finishing || total_bytes != before_) {
            throw std::runtime_error("OBS recording packet acceptance could not be confirmed. Voice stems stopped.");
        }
    }
    if (finishing) {
        buffered_clock_.reset();
        buffered_decode_.reset();
    }
    return committed;
}
void PacketAcceptance::submit(uint64_t total_bytes, uint64_t size, std::optional<Clock> clock, uint64_t header_bytes,
                              std::optional<VideoDecodeTime> decode)
{
    if (buffered_video_ && clock && (!decode || decode->denominator <= 0))
        throw std::runtime_error("OBS hybrid video decode timing is unavailable. Voice stems stopped.");
    if (pending_ || !size || total_bytes > UINT64_MAX - header_bytes || total_bytes + header_bytes > UINT64_MAX - size)
        throw std::runtime_error("Invalid OBS recording packet accounting.");
    before_ = total_bytes + header_bytes;
    size_ = size;
    clock_ = clock;
    decode_ = decode;
    pending_ = true;
}
void PacketAcceptance::reset(bool buffered_video) noexcept
{
    pending_ = false;
    clock_.reset();
    buffered_video_ = buffered_video;
    buffered_clock_.reset();
    buffered_decode_.reset();
    decode_.reset();
}
void CaptureDrain::start(uint64_t end, uint64_t now)
{
    end_ = end;
    deadline_ = now > UINT64_MAX - 10000000000ULL ? UINT64_MAX : now + 10000000000ULL;
}
bool CaptureDrain::ready(uint64_t watermark) const noexcept { return watermark >= end_; }
void CaptureDrain::expire(uint64_t now) const
{
    if (now >= deadline_) throw std::runtime_error("Perfect Comms did not drain audio through the final accepted OBS frame. Voice stems may be incomplete.");
}
}
