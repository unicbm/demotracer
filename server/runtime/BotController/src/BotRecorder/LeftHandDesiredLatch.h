#pragma once

#include <atomic>
#include <cstdint>

namespace BotController
{
    // A held command preference, independent of replay movement ownership.
    // Bind it to an entity incarnation so a reused slot cannot inherit it.
    class LeftHandDesiredLatch
    {
    public:
        void Clear() { enabled_.store(false, std::memory_order_release); }

        void Set(uintptr_t pawn, uint32_t handle, bool desired)
        {
            Clear();
            pawn_.store(pawn, std::memory_order_relaxed);
            handle_.store(handle, std::memory_order_relaxed);
            desired_.store(desired, std::memory_order_relaxed);
            enabled_.store(true, std::memory_order_release);
        }

        bool Get(bool &desired) const
        {
            if (!enabled_.load(std::memory_order_acquire)) return false;
            desired = desired_.load(std::memory_order_relaxed);
            return true;
        }

        bool Validate(uintptr_t pawn, uint32_t handle, bool autonomousAliveBot)
        {
            if (!enabled_.load(std::memory_order_acquire)) return false;
            if (!autonomousAliveBot || !pawn ||
                pawn != pawn_.load(std::memory_order_relaxed) ||
                handle != handle_.load(std::memory_order_relaxed))
            {
                Clear();
                return false;
            }
            return true;
        }

        void ObserveReplayCommand(bool desired)
        {
            if (enabled_.load(std::memory_order_acquire))
                desired_.store(desired, std::memory_order_relaxed);
        }

    private:
        std::atomic<bool> enabled_{false};
        std::atomic<bool> desired_{false};
        std::atomic<uintptr_t> pawn_{0};
        std::atomic<uint32_t> handle_{0};
    };
}
