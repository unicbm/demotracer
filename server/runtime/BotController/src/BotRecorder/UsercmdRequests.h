#pragma once

#include "ButtonState.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <mutex>
#include <vector>

namespace BotController
{
    // The public controller API owns independent requests; DTR replay owns the
    // whole slot and clears this store at every ownership boundary.
    class UsercmdRequests
    {
    public:
        enum class Kind { Injection, Movement, Suppression };
        struct Frame
        {
            ButtonState::Planes buttons;
            uint64_t controlledMask;
            bool movement;
            float forward;
            float left;
        };
        static constexpr uint64_t MovementMask = (1ULL << 3) | (1ULL << 4) | (1ULL << 9) | (1ULL << 10);

        int64_t Add(int slot, Kind kind, uint64_t buttons, int durationMs,
                    int64_t now, float forward = 0, float left = 0)
        {
            if (!Valid(slot) || durationMs < 0 || !std::isfinite(forward) || !std::isfinite(left) ||
                (kind != Kind::Movement && buttons == 0))
                return -1;
            std::scoped_lock lock(mutex_);
            auto id = nextId_++;
            slots_[slot].requests.push_back({id, kind, buttons, durationMs,
                kind == Kind::Suppression && durationMs > 0 ? now + durationMs : 0,
                false, std::clamp(forward, -1.0f, 1.0f), std::clamp(left, -1.0f, 1.0f)});
            return id;
        }

        bool Cancel(int slot, Kind kind, int64_t id)
        {
            if (!Valid(slot) || id <= 0) return false;
            std::scoped_lock lock(mutex_);
            auto &requests = slots_[slot].requests;
            return std::erase_if(requests, [=](const auto &request) {
                return request.id == id && request.kind == kind;
            }) != 0;
        }

        bool UpdateMovement(int slot, int64_t id, float forward, float left)
        {
            if (!Valid(slot) || !std::isfinite(forward) || !std::isfinite(left)) return false;
            std::scoped_lock lock(mutex_);
            for (auto &request : slots_[slot].requests)
            {
                if (request.id != id || request.kind != Kind::Movement) continue;
                request.forward = std::clamp(forward, -1.0f, 1.0f);
                request.left = std::clamp(left, -1.0f, 1.0f);
                return true;
            }
            return false;
        }

        bool Pending(int slot)
        {
            if (!Valid(slot)) return false;
            std::scoped_lock lock(mutex_);
            return !slots_[slot].requests.empty() || slots_[slot].previousHeld != 0;
        }

        Frame Advance(int slot, int64_t now, ButtonState::Planes original)
        {
            std::scoped_lock lock(mutex_);
            auto &state = slots_[slot];
            uint64_t injected = 0, suppressed = 0;
            Frame frame{original, 0, false, 0, 0};
            std::erase_if(state.requests, [=](const auto &request) {
                return (request.kind == Kind::Injection && request.started &&
                        (request.durationMs == 0 || now >= request.expires)) ||
                       (request.kind == Kind::Suppression && request.expires > 0 && now >= request.expires);
            });
            for (auto &request : state.requests)
            {
                if (request.kind == Kind::Injection)
                {
                    injected |= request.buttons;
                    if (!request.started)
                    {
                        request.started = true;
                        request.expires = now + request.durationMs;
                    }
                }
                else if (request.kind == Kind::Suppression)
                    suppressed |= request.buttons;
                else
                {
                    // Last-created movement request wins until it is cancelled.
                    frame.movement = true;
                    frame.forward = request.forward;
                    frame.left = request.left;
                }
            }
            uint64_t movement = 0;
            if (frame.forward > 0) movement |= 1ULL << 3;
            if (frame.forward < 0) movement |= 1ULL << 4;
            if (frame.left > 0) movement |= 1ULL << 9;
            if (frame.left < 0) movement |= 1ULL << 10;
            const uint64_t held = (injected | movement) & ~suppressed;
            frame.controlledMask = injected | movement | state.previousHeld | suppressed |
                (frame.movement ? MovementMask : 0);
            const auto transitions = ButtonState::EncodeAdjacentHeld(held, state.previousHeld);
            frame.buttons.state1 = (original.state1 & ~frame.controlledMask) | held;
            frame.buttons.state2 = (original.state2 & ~frame.controlledMask) |
                transitions.state2 | (original.state1 & suppressed);
            frame.buttons.state3 = original.state3 & ~frame.controlledMask;
            state.previousHeld = held;
            return frame;
        }

        void Clear(int slot)
        {
            if (!Valid(slot)) return;
            std::scoped_lock lock(mutex_);
            slots_[slot] = {};
        }
        void ClearAll()
        {
            std::scoped_lock lock(mutex_);
            for (auto &slot : slots_) slot = {};
        }

    private:
        static bool Valid(int slot) { return slot >= 0 && slot < 64; }
        struct Request
        {
            int64_t id;
            Kind kind;
            uint64_t buttons;
            int durationMs;
            int64_t expires;
            bool started;
            float forward, left;
        };
        struct Slot { std::vector<Request> requests; uint64_t previousHeld = 0; };
        std::array<Slot, 64> slots_{};
        int64_t nextId_ = 1;
        std::mutex mutex_;
    };
}
